using Blade.Source;

namespace Blade.Diagnostics;

public enum DiagnosticSeverity
{
    Error,
    Warning,
    Info,
}

/// <summary>
/// A single diagnostic message with code, location, and human-readable text.
/// </summary>
public sealed class Diagnostic
{
    public DiagnosticCode Code { get; }
    public TextSpan Span { get; }
    public string Message { get; }

    public Diagnostic(DiagnosticCode code, TextSpan span, string message)
    {
        Code = code;
        Span = span;
        Message = message;
    }

    public DiagnosticSeverity Severity => GetSeverity(Code);
    public bool IsError => Severity == DiagnosticSeverity.Error;

    public string FormatCode()
    {
        int numericCode = (int)Code;
        return $"{GetSeverityPrefix(Code)}{numericCode:D4}";
    }

    public override string ToString() => $"{FormatCode()}: {Message}";

    private static char GetSeverityPrefix(DiagnosticCode code)
    {
        string? codeName = System.Enum.GetName(code);
        if (!string.IsNullOrEmpty(codeName))
        {
            char prefix = codeName[0];
            if (prefix is 'E' or 'W' or 'I')
                return prefix;
        }

        return 'E';
    }

    public static DiagnosticSeverity GetSeverity(DiagnosticCode code)
    {
        string? codeName = System.Enum.GetName(code);
        if (!string.IsNullOrEmpty(codeName))
        {
            return codeName[0] switch
            {
                'W' => DiagnosticSeverity.Warning,
                'I' => DiagnosticSeverity.Info,
                _ => DiagnosticSeverity.Error,
            };
        }

        return DiagnosticSeverity.Error;
    }
}
