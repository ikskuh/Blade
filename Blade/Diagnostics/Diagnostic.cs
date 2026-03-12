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
        return $"E{numericCode:D4}";
    }

    public override string ToString() => $"{FormatCode()}: {Message}";
}
