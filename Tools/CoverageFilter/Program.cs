using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Tools.CoverageFilter;

internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitUsageError = 2;
    private const int ExitIoOrXmlError = 3;

    private static readonly Regex ForceCoverageRegex = new(
        @"//\s*pragma\s*:\s*force-coverage\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static int Main(string[] args)
    {
        string? coberturaPath = null;
        if (args.Length == 0)
        {
            coberturaPath = Path.Combine("coverage", "coverage.cobertura.xml");
        }
        else if (args.Length == 1)
        {
            if (args[0] is "-h" or "--help")
            {
                PrintUsage();
                return ExitSuccess;
            }

            coberturaPath = args[0];
        }
        else
        {
            PrintUsage();
            return ExitUsageError;
        }

        if (string.IsNullOrWhiteSpace(coberturaPath))
        {
            Console.Error.WriteLine("error: cobertura path is empty.");
            return ExitUsageError;
        }

        try
        {
            return Run(coberturaPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitIoOrXmlError;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run --project Tools/CoverageFilter -- [path-to-cobertura.xml]");
        Console.WriteLine("Default path: coverage/coverage.cobertura.xml");
    }

    private static int Run(string coberturaPath)
    {
        if (!File.Exists(coberturaPath))
        {
            Console.Error.WriteLine($"error: file not found: {coberturaPath}");
            return ExitIoOrXmlError;
        }

        Encoding outputEncoding = DetectOutputEncoding(coberturaPath);

        string xmlText = File.ReadAllText(coberturaPath, outputEncoding);

        List<int> lineStarts = ComputeLineStarts(xmlText);
        ParsedCobertura cobertura = ParseCobertura(xmlText);

        if (cobertura.Candidates.Count == 0)
        {
            Console.WriteLine("CoverageFilter: no uncovered lines found (hits=\"0\").");
            return ExitSuccess;
        }

        Dictionary<string, string[]> fileLinesCache = new(StringComparer.Ordinal);
        Dictionary<string, string?> resolvedPathCache = new(StringComparer.Ordinal);

        char[] xmlChars = xmlText.ToCharArray();
        int flips = 0;
        int matchedPragmas = 0;

        foreach (CoberturaLineCandidate candidate in cobertura.Candidates)
        {
            if (!TryResolveSourcePath(
                    coberturaPath,
                    candidate.ClassFilename,
                    cobertura.SourceRoots,
                    resolvedPathCache,
                    out string sourcePath))
            {
                continue;
            }

            if (!TryGetSourceLine(sourcePath, candidate.SourceLineNumber, fileLinesCache, out string sourceLine))
            {
                continue;
            }

            if (!ForceCoverageRegex.IsMatch(sourceLine))
            {
                continue;
            }

            matchedPragmas++;

            if (!TryGetXmlIndexFromLineInfo(lineStarts, xmlText.Length, candidate.XmlLineNumber, candidate.XmlLinePosition, out int xmlIndex))
            {
                continue;
            }

            if (!TryFlipHitsAttribute(xmlChars, xmlText, xmlIndex, candidate.SourceLineNumber))
            {
                continue;
            }

            flips++;
        }

        if (flips == 0)
        {
            Console.WriteLine($"CoverageFilter: matched {matchedPragmas} pragma line(s); no XML edits needed.");
            return ExitSuccess;
        }

        string updatedXml = new(xmlChars);
        File.WriteAllText(coberturaPath, updatedXml, outputEncoding);
        Console.WriteLine($"CoverageFilter: set hits=\"1\" for {flips} line element(s) ({matchedPragmas} pragma match(es)).");
        return ExitSuccess;
    }

    private static Encoding DetectOutputEncoding(string path)
    {
        using FileStream stream = File.OpenRead(path);

        Span<byte> header = stackalloc byte[4];
        int bytesRead = stream.Read(header);
        if (bytesRead < 2)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        if (header[0] == 0xFF && header[1] == 0xFE)
        {
            return Encoding.Unicode;
        }

        if (header[0] == 0xFE && header[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode;
        }

        if (bytesRead >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    private static List<int> ComputeLineStarts(string text)
    {
        List<int> lineStarts = new(capacity: 1024) { 0 };

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                if (i + 1 < text.Length)
                {
                    lineStarts.Add(i + 1);
                }

                continue;
            }

            if (c == '\n')
            {
                if (i + 1 < text.Length)
                {
                    lineStarts.Add(i + 1);
                }
            }
        }

        return lineStarts;
    }

    private static bool TryGetXmlIndexFromLineInfo(
        List<int> lineStarts,
        int textLength,
        int xmlLineNumber,
        int xmlLinePosition,
        out int index)
    {
        index = 0;

        if (xmlLineNumber <= 0 || xmlLinePosition <= 0)
        {
            return false;
        }

        int lineIndex = xmlLineNumber - 1;
        if ((uint)lineIndex >= (uint)lineStarts.Count)
        {
            return false;
        }

        int lineStart = lineStarts[lineIndex];
        index = lineStart + (xmlLinePosition - 1);
        if ((uint)index >= (uint)textLength)
        {
            return false;
        }

        return true;
    }

    private static ParsedCobertura ParseCobertura(string xmlText)
    {
        List<string> sourceRoots = new();
        List<CoberturaLineCandidate> candidates = new();

        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreWhitespace = false,
            IgnoreComments = false,
            IgnoreProcessingInstructions = false,
            CloseInput = true,
        };

        using StringReader stringReader = new(xmlText);
        using XmlReader reader = XmlReader.Create(stringReader, settings);

        IXmlLineInfo lineInfo = (IXmlLineInfo)reader;

        string? currentClassFilename = null;
        int currentClassDepth = -1;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                string localName = reader.LocalName;

                if (localName == "source")
                {
                    string source = reader.ReadElementContentAsString();
                    if (!string.IsNullOrWhiteSpace(source))
                    {
                        sourceRoots.Add(source.Trim());
                    }

                    continue;
                }

                if (localName == "class")
                {
                    string? filename = reader.GetAttribute("filename");
                    if (!string.IsNullOrWhiteSpace(filename))
                    {
                        currentClassFilename = filename;
                        currentClassDepth = reader.Depth;
                    }

                    continue;
                }

                if (localName == "line")
                {
                    if (currentClassFilename is null)
                    {
                        continue;
                    }

                    string? hitsText = reader.GetAttribute("hits");
                    if (!string.Equals(hitsText, "0", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string? numberText = reader.GetAttribute("number");
                    if (!int.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out int sourceLineNumber))
                    {
                        continue;
                    }

                    candidates.Add(
                        new CoberturaLineCandidate(
                            currentClassFilename,
                            sourceLineNumber,
                            lineInfo.LineNumber,
                            lineInfo.LinePosition));
                }

                continue;
            }

            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName == "class" && reader.Depth == currentClassDepth)
                {
                    currentClassFilename = null;
                    currentClassDepth = -1;
                }
            }
        }

        return new ParsedCobertura(sourceRoots, candidates);
    }

    private static bool TryResolveSourcePath(
        string coberturaPath,
        string classFilename,
        List<string> sourceRoots,
        Dictionary<string, string?> resolvedPathCache,
        out string resolvedPath)
    {
        if (resolvedPathCache.TryGetValue(classFilename, out string? cached))
        {
            if (cached is not null)
            {
                resolvedPath = cached;
                return true;
            }

            resolvedPath = string.Empty;
            return false;
        }

        if (Path.IsPathRooted(classFilename))
        {
            if (File.Exists(classFilename))
            {
                resolvedPath = classFilename;
                resolvedPathCache[classFilename] = resolvedPath;
                return true;
            }

            resolvedPath = string.Empty;
            resolvedPathCache[classFilename] = null;
            return false;
        }

        foreach (string root in sourceRoots)
        {
            string candidate = Path.Combine(root, classFilename);
            if (File.Exists(candidate))
            {
                resolvedPath = candidate;
                resolvedPathCache[classFilename] = resolvedPath;
                return true;
            }
        }

        string coberturaDir = Path.GetDirectoryName(coberturaPath) ?? ".";
        string fallbackCandidate = Path.Combine(coberturaDir, classFilename);
        if (File.Exists(fallbackCandidate))
        {
            resolvedPath = fallbackCandidate;
            resolvedPathCache[classFilename] = resolvedPath;
            return true;
        }

        resolvedPath = string.Empty;
        resolvedPathCache[classFilename] = null;
        return false;
    }

    private static bool TryGetSourceLine(
        string sourcePath,
        int sourceLineNumber,
        Dictionary<string, string[]> fileLinesCache,
        out string line)
    {
        line = string.Empty;

        if (sourceLineNumber <= 0)
        {
            return false;
        }

        if (!fileLinesCache.TryGetValue(sourcePath, out string[]? lines))
        {
            if (!File.Exists(sourcePath))
            {
                return false;
            }

            lines = File.ReadAllLines(sourcePath);
            fileLinesCache[sourcePath] = lines;
        }

        int index = sourceLineNumber - 1;
        if ((uint)index >= (uint)lines.Length)
        {
            return false;
        }

        line = lines[index];
        return true;
    }

    private static bool TryFlipHitsAttribute(char[] xmlChars, string xmlText, int xmlIndex, int expectedNumber)
    {
        int searchStart = Math.Max(0, xmlIndex - 50);
        int searchLimit = Math.Min(xmlText.Length, xmlIndex + 800);

        int searchIndex = searchStart;
        while (true)
        {
            int openIndex = xmlText.IndexOf("<line", searchIndex, StringComparison.Ordinal);
            if (openIndex < 0 || openIndex > searchLimit)
            {
                return false;
            }

            int closeIndex = xmlText.IndexOf('>', openIndex);
            if (closeIndex < 0)
            {
                return false;
            }

            if (!TryParseIntAttribute(xmlChars, openIndex, closeIndex, "number", out int numberValue))
            {
                searchIndex = openIndex + 5;
                continue;
            }

            if (numberValue != expectedNumber)
            {
                searchIndex = openIndex + 5;
                continue;
            }

            if (!TryGetStringAttributeValue(xmlChars, openIndex, closeIndex, "hits", out int hitsValueIndex))
            {
                return false;
            }

            if (xmlChars[hitsValueIndex] != '0')
            {
                return false;
            }

            xmlChars[hitsValueIndex] = '1';
            return true;
        }
    }

    private static bool TryParseIntAttribute(
        char[] xmlChars,
        int elementOpenIndex,
        int elementCloseIndex,
        string attributeName,
        out int value)
    {
        value = 0;

        if (!TryGetStringAttributeValue(xmlChars, elementOpenIndex, elementCloseIndex, attributeName, out int valueIndex))
        {
            return false;
        }

        int i = valueIndex;
        int end = elementCloseIndex;

        bool any = false;
        int parsed = 0;
        while (i < end)
        {
            char c = xmlChars[i];
            if (c is < '0' or > '9')
            {
                break;
            }

            any = true;
            parsed = checked((parsed * 10) + (c - '0'));
            i++;
        }

        if (!any)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryGetStringAttributeValue(
        char[] xmlChars,
        int elementOpenIndex,
        int elementCloseIndex,
        string attributeName,
        out int valueIndex)
    {
        valueIndex = 0;

        int i = elementOpenIndex;
        int end = elementCloseIndex;

        while (i < end)
        {
            i = SkipUntilPotentialAttribute(xmlChars, i, end);
            if (i >= end)
            {
                return false;
            }

            if (!IsAttributeNameAt(xmlChars, i, end, attributeName))
            {
                i++;
                continue;
            }

            int afterName = i + attributeName.Length;
            int j = SkipWhitespace(xmlChars, afterName, end);
            if (j >= end || xmlChars[j] != '=')
            {
                i++;
                continue;
            }

            j = SkipWhitespace(xmlChars, j + 1, end);
            if (j >= end)
            {
                return false;
            }

            char quote = xmlChars[j];
            if (quote is not '"' and not '\'')
            {
                i++;
                continue;
            }

            if (j + 1 >= end)
            {
                return false;
            }

            valueIndex = j + 1;
            return true;
        }

        return false;
    }

    private static int SkipUntilPotentialAttribute(char[] xmlChars, int start, int end)
    {
        int i = start;
        while (i < end)
        {
            char c = xmlChars[i];
            if (char.IsLetter(c) || c == '_')
            {
                return i;
            }

            i++;
        }

        return end;
    }

    private static bool IsAttributeNameAt(char[] xmlChars, int index, int end, string attributeName)
    {
        if (index + attributeName.Length > end)
        {
            return false;
        }

        for (int i = 0; i < attributeName.Length; i++)
        {
            if (xmlChars[index + i] != attributeName[i])
            {
                return false;
            }
        }

        int after = index + attributeName.Length;
        if (after >= end)
        {
            return true;
        }

        char next = xmlChars[after];
        return next is ' ' or '\t' or '\r' or '\n' or '=';
    }

    private static int SkipWhitespace(char[] xmlChars, int start, int end)
    {
        int i = start;
        while (i < end)
        {
            char c = xmlChars[i];
            if (c is not (' ' or '\t' or '\r' or '\n'))
            {
                return i;
            }

            i++;
        }

        return end;
    }

    private readonly record struct ParsedCobertura(List<string> SourceRoots, List<CoberturaLineCandidate> Candidates);

    private readonly record struct CoberturaLineCandidate(
        string ClassFilename,
        int SourceLineNumber,
        int XmlLineNumber,
        int XmlLinePosition);
}
