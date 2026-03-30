using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Blade;

public sealed class RuntimeTemplate
{
    public const string ConMarker = "<<BLADE_CON>>";
    public const string DatMarker = "<<BLADE_DAT>>";
    public const string HaltLabel = "blade_halt";
    private static readonly Regex ConMarkerRegex = new(@"^\s*'\s*<<BLADE_CON>>\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex DatMarkerRegex = new(@"^\s*'\s*<<BLADE_DAT>>\s*$", RegexOptions.CultureInvariant);

    private RuntimeTemplate(string sourcePath, string templateText)
    {
        SourcePath = Requires.NotNull(sourcePath);
        TemplateText = Requires.NotNull(templateText);
    }

    public string SourcePath { get; }
    public string TemplateText { get; }

    public static bool TryLoad(string path, out RuntimeTemplate? template, out string? errorMessage)
    {
        Requires.NotNull(path);

        template = null;
        errorMessage = null;

        string fullPath = Path.GetFullPath(path);
        string text;
        try
        {
            text = File.ReadAllText(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errorMessage = $"error: failed to read runtime template '{fullPath}': {ex.Message}";
            return false;
        }

        if (!ValidateTemplate(text, fullPath, out errorMessage))
            return false;

        template = new RuntimeTemplate(fullPath, text);
        return true;
    }

    private static bool ValidateTemplate(string text, string sourcePath, out string? errorMessage)
    {
        Requires.NotNull(text);
        Requires.NotNull(sourcePath);

        int conMarkerCount = 0;
        int datMarkerCount = 0;
        string[] lines = text.Split('\n');
        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');
            if (IsConMarkerLine(line))
                conMarkerCount++;
            if (IsDatMarkerLine(line))
                datMarkerCount++;
        }

        if (conMarkerCount != 1)
        {
            errorMessage = $"error: runtime template '{sourcePath}' must contain exactly one special comment marker for {ConMarker}.";
            return false;
        }

        if (datMarkerCount != 1)
        {
            errorMessage = $"error: runtime template '{sourcePath}' must contain exactly one special comment marker for {DatMarker}.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    public static bool IsConMarkerLine(string line)
    {
        Requires.NotNull(line);
        return ConMarkerRegex.IsMatch(line);
    }

    public static bool IsDatMarkerLine(string line)
    {
        Requires.NotNull(line);
        return DatMarkerRegex.IsMatch(line);
    }
}
