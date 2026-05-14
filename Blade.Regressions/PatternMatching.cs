using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Blade;

namespace Blade.Regressions;

/// <summary>Represents one normalized code line as a sequence of space-delimited segments.</summary>
public sealed record class NormalizedText(IReadOnlyList<string> Segments)
{
    /// <summary>Gets the single-line normalized text rendered from the stored segments.</summary>
    public string Text => string.Join(' ', Segments);
}

/// <summary>Represents one normalized source line together with its original source line number.</summary>
public sealed record class NormalizedSourceLine(NormalizedText Text, int SourceLineNumber);

/// <summary>Represents an ordered collection of normalized source lines for matcher scans.</summary>
public sealed class NormalizedSourceText(IReadOnlyList<NormalizedSourceLine> lines)
{
    /// <summary>Gets the normalized source lines that participate in matching.</summary>
    public IReadOnlyList<NormalizedSourceLine> Lines { get; } = lines;

    /// <summary>Gets the number of normalized lines represented by the haystack.</summary>
    public int LineCount => Lines.Count;

    /// <summary>Gets the normalized source line at the supplied line index.</summary>
    public NormalizedSourceLine GetLine(int lineIndex)
    {
        return Lines[lineIndex];
    }

    /// <summary>Gets the normalized gap text between two line indexes.</summary>
    public string GetGapText(int startLineIndex, int endLineIndex)
    {
        if (startLineIndex >= endLineIndex)
            return string.Empty;

        return string.Join(
            Environment.NewLine,
            Lines.Skip(startLineIndex).Take(endLineIndex - startLineIndex).Select(static line => line.Text.Text));
    }
}

/// <summary>Normalizes compiler and assembly text for snippet matching.</summary>
public static class CodeNormalizer
{
    public delegate string CommentStripper(string text);

    private static readonly Regex WordBoundaryRegex = new(@"\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Normalizes text emitted from a specific compiler stage into a line-oriented haystack.</summary>
    public static NormalizedSourceText NormalizeBladeStage(RegressionStage stage, string text)
    {
        return NormalizeSourceText(text, GetCommentStripper(stage));
    }

    public static NormalizedSourceText NormalizeSourceText(string text) => NormalizeSourceText(text, static t => t);

    /// <summary>Normalizes arbitrary multi-line text into normalized source lines.</summary>
    public static NormalizedSourceText NormalizeSourceText(string text, CommentStripper stripComments)
    {
        List<NormalizedSourceLine> lines = [];
        int sourceLineNumber = 0;

        foreach (string rawLine in SplitLines(text))
        {
            sourceLineNumber += 1;
            var line = stripComments(rawLine).TrimEnd();
            if (line.Length == 0)
                continue;

            NormalizedText normalizedLine = NormalizeText(line);
            if (normalizedLine.Segments.Count == 0)
                continue;

            lines.Add(new NormalizedSourceLine(normalizedLine, sourceLineNumber));
        }

        return new NormalizedSourceText(lines);
    }

    /// <summary>Normalizes a single line of text into ordered segments.</summary>
    public static NormalizedText NormalizeText(string text)
    {
        Requires.That(
            text.IndexOfAny(['\r', '\n']) < 0,
            "Normalized matcher lines must not contain line feeds.");

        string separated = WordBoundaryRegex.Replace(text, " ");
        string collapsed = WhitespaceRegex.Replace(separated, " ");
        string trimmed = collapsed.Trim();
        if (trimmed.Length == 0)
            return new NormalizedText([]);

        return new NormalizedText(trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Strips the stage-appropriate comment syntax before normalization.</summary>
    public static CommentStripper GetCommentStripper(RegressionStage stage)
    {
        return stage switch
        {
            RegressionStage.Bound => StripSemicolonComment,
            RegressionStage.MirPreOptimization => StripSemicolonComment,
            RegressionStage.Mir => StripSemicolonComment,
            RegressionStage.LirPreOptimization => StripSemicolonComment,
            RegressionStage.Lir => StripSemicolonComment,
            RegressionStage.AsmirPreOptimization => StripAssemblyComment,
            RegressionStage.AsmirPreRegisterAllocation => StripAssemblyComment,
            RegressionStage.Asmir => StripAssemblyComment,
            RegressionStage.FinalAsm => StripAssemblyComment,
            _ => throw new InvalidOperationException($"Unknown stage '{stage}'."),
        };
    }

    private static string StripSemicolonComment(string line) => StripComment(line, ";");

    private static string StripAssemblyComment(string line) => StripComment(line, "'");

    private static string StripComment(string text, string token)
    {
        int commentIndex = text.IndexOf(token, StringComparison.Ordinal);
        return commentIndex >= 0 ? text[..commentIndex] : text;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            int lineFeedIndex = text.IndexOf('\n', index);
            if (lineFeedIndex < 0)
            {
                yield return text[index..];
                break;
            }

            yield return text[index..lineFeedIndex].TrimEnd('\r');
            index = lineFeedIndex + 1;
        }
    }
}

/// <summary>Finds snippet patterns within normalized stage text.</summary>
public static class SnippetMatcher
{
    /// <summary>Determines whether the haystack contains at least one match for the pattern.</summary>
    public static bool Contains(NormalizedSourceText haystack, Pattern pattern, PatternBindings bindings)
    {
        return IndexOf(haystack, pattern, 0, bindings) is not null;
    }

    /// <summary>Finds the next match for the pattern starting at the supplied line index.</summary>
    public static PatternMatch? IndexOf(NormalizedSourceText haystack, Pattern pattern, int startIndex, PatternBindings bindings)
    {
        return IndexOf(haystack, pattern, startIndex, haystack.LineCount, bindings);
    }

    /// <summary>Finds the next match for the pattern within a half-open line range.</summary>
    public static PatternMatch? IndexOf(NormalizedSourceText haystack, Pattern pattern, int startIndex, int endIndexExclusive, PatternBindings bindings)
    {
        for (int lineIndex = startIndex; lineIndex < endIndexExclusive; lineIndex++)
        {
            PatternBindings candidateBindings = bindings.Clone();
            if (pattern.TryMatchLine(haystack.GetLine(lineIndex).Text, candidateBindings))
            {
                bindings.ReplaceWith(candidateBindings);
                return new PatternMatch(lineIndex, lineIndex + 1);
            }
        }

        return null;
    }

    /// <summary>Counts non-overlapping occurrences of the pattern in the haystack.</summary>
    public static int CountOccurrences(NormalizedSourceText haystack, Pattern pattern, PatternBindings bindings)
    {
        int count = 0;
        int index = 0;
        PatternBindings countBindings = bindings.Clone();
        while (index < haystack.LineCount)
        {
            if (IndexOf(haystack, pattern, index, countBindings) is not PatternMatch match)
                break;

            count++;
            index = match.EndLineIndexExclusive;
        }

        if (count > 0)
            bindings.ReplaceWith(countBindings);

        return count;
    }
}

/// <summary>Represents one normalized-text match span reported by the snippet matcher.</summary>
public readonly record struct PatternMatch(int StartLineIndex, int EndLineIndexExclusive);

/// <summary>Tracks numbered wildcard bindings while a snippet pattern is matched.</summary>
public sealed class PatternBindings
{
    private readonly Dictionary<int, string> _bindings = [];

    /// <summary>Initializes an empty set of wildcard bindings.</summary>
    public PatternBindings() { }

    private PatternBindings(Dictionary<int, string> bindings)
    {
        _bindings = new Dictionary<int, string>(bindings);
    }

    /// <summary>Clones the current binding set for speculative matching.</summary>
    public PatternBindings Clone()
    {
        return new PatternBindings(_bindings);
    }

    /// <summary>Attempts to bind or validate a numbered wildcard capture.</summary>
    public bool TryBind(int number, string value)
    {
        if (_bindings.TryGetValue(number, out string? bound))
            return string.Equals(bound, value, StringComparison.Ordinal);

        _bindings.Add(number, value);
        return true;
    }

    /// <summary>Replaces the current bindings with another binding set.</summary>
    public void ReplaceWith(PatternBindings other)
    {
        _bindings.Clear();
        foreach ((int key, string value) in other._bindings)
            _bindings.Add(key, value);
    }

    /// <summary>Creates an ordered snapshot of the current wildcard bindings.</summary>
    public IReadOnlyList<PatternBindingCapture> Snapshot()
    {
        return _bindings
            .OrderBy(static entry => entry.Key)
            .Select(static entry => new PatternBindingCapture(entry.Key, entry.Value))
            .ToArray();
    }
}

/// <summary>Represents one captured numbered wildcard binding.</summary>
public readonly record struct PatternBindingCapture(int Number, string Value);

/// <summary>Compiles and matches single-line snippet patterns with wildcard captures.</summary>
public sealed record class Pattern(string Source, IReadOnlyList<PatternPart> Parts)
{
    private static readonly Regex WildcardTokenRegex = new(@"\?(\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Compiles raw snippet text into a normalized segment pattern.</summary>
    public static Pattern Compile(string text)
    {
        Requires.That(text.IndexOfAny(['\r', '\n']) < 0, "Snippet patterns must not contain line feeds.");

        List<PatternPart> parts = [];
        int lastEnd = 0;
        MatchCollection wildcardMatches = WildcardTokenRegex.Matches(text);

        foreach (Match wildcardMatch in wildcardMatches)
        {
            AddLiteralParts(parts, text[lastEnd..wildcardMatch.Index]);
            int? binding = wildcardMatch.Groups[1].Success
                ? int.Parse(wildcardMatch.Groups[1].Value, CultureInfo.InvariantCulture)
                : null;
            parts.Add(PatternPart.CreateWildcard(binding));
            lastEnd = wildcardMatch.Index + wildcardMatch.Length;
        }

        AddLiteralParts(parts, text[lastEnd..]);
        return new Pattern(text, parts);
    }

    /// <summary>Attempts to match the compiled pattern against one normalized line.</summary>
    public bool TryMatchLine(NormalizedText line, PatternBindings bindings)
    {
        if (line.Segments.Count != Parts.Count)
            return false;

        for (int index = 0; index < Parts.Count; index++)
        {
            PatternPart part = Parts[index];
            string segment = line.Segments[index];
            if (part.IsWildcard)
            {
                if (!IsIdentifierSegment(segment))
                    return false;

                if (part.BindingNumber is int bindingNumber && !bindings.TryBind(bindingNumber, segment))
                    return false;
            }
            else
            {
                if (!string.Equals(segment, part.Literal, StringComparison.Ordinal))
                    return false;
            }
        }

        return true;
    }

    private static void AddLiteralParts(List<PatternPart> parts, string text)
    {
        NormalizedText normalized = CodeNormalizer.NormalizeText(text);
        foreach (string segment in normalized.Segments)
            parts.Add(PatternPart.CreateLiteral(segment));
    }

    private static bool IsIdentifierSegment(string segment)
    {
        Debug.Assert(segment.Length > 0, "Wildcard segments must have at least one character.");
        foreach (char ch in segment)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_')
                return false;
        }

        return true;
    }
}

/// <summary>Represents one compiled segment in a single-line pattern.</summary>
public readonly record struct PatternPart
{
    /// <summary>Creates a literal segment matcher.</summary>
    public static PatternPart CreateLiteral(string literal)
    {
        return new PatternPart(literal, null);
    }

    /// <summary>Creates a wildcard segment matcher.</summary>
    public static PatternPart CreateWildcard(int? bindingNumber)
    {
        return new PatternPart(null, bindingNumber);
    }

    private PatternPart(string? literal, int? bindingNumber)
    {
        this.Literal = literal;
        this.BindingNumber = bindingNumber;
    }

    /// <summary>
    /// Gets whether the part represents a wildcard segment that can match any identifier and capture a binding.
    /// </summary>
    public bool IsWildcard => this.Literal is null;

    public string? Literal { get; }
    public int? BindingNumber { get; }
}
