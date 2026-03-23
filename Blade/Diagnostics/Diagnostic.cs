using Blade.Source;

namespace Blade.Diagnostics;

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
}
