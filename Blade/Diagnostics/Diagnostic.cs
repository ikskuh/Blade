using Blade;
using Blade.Source;

namespace Blade.Diagnostics;

public enum DiagnosticSeverity
{
    Note,
    Warning,
    Error,
}

/// <summary>
/// A reported diagnostic message, optionally associated with a source location.
/// </summary>
public sealed class Diagnostic
{
    private static readonly SourceText UnlocatedSource = new(string.Empty, "<diagnostic>");

    /// <summary>
    /// Creates a diagnostic from a typed diagnostic message.
    /// </summary>
    public Diagnostic(DiagnosticMessage message)
    {
        DiagnosticMessage = Requires.NotNull(message);
        if (message is LocatedDiagnosticMessage located)
        {
            Source = located.Source;
            Span = located.Span;
            IsLocated = true;
        }
        else
        {
            Source = UnlocatedSource;
            Span = new TextSpan(0, 0);
            IsLocated = false;
        }
    }

    /// <summary>
    /// Gets the typed diagnostic message.
    /// </summary>
    public DiagnosticMessage DiagnosticMessage { get; }

    /// <summary>
    /// Gets the source text associated with the diagnostic, or a placeholder for generic diagnostics.
    /// </summary>
    public SourceText Source { get; }

    /// <summary>
    /// Gets the formatted diagnostic code.
    /// </summary>
    public string Code => FormatCode();

    /// <summary>
    /// Gets the diagnostic name without severity suffix.
    /// </summary>
    public string Name => DiagnosticMessage.Name;

    /// <summary>
    /// Gets the source span associated with the diagnostic, or an empty placeholder span for generic diagnostics.
    /// </summary>
    public TextSpan Span { get; }

    /// <summary>
    /// Gets the human-readable diagnostic text.
    /// </summary>
    public string Message => DiagnosticMessage.Message;

    /// <summary>
    /// Gets whether this diagnostic is associated with a source span.
    /// </summary>
    public bool IsLocated { get; }

    /// <summary>
    /// Gets the diagnostic severity derived from the diagnostic code prefix.
    /// </summary>
    public DiagnosticSeverity Severity => DiagnosticMessage.Severity;

    /// <summary>
    /// Gets whether the diagnostic severity is an error.
    /// </summary>
    public bool IsError => Severity == DiagnosticSeverity.Error;

    /// <summary>
    /// Gets the source location where this located diagnostic starts.
    /// </summary>
    public SourceLocation GetLocation()
    {
        Assert.Invariant(IsLocated, "Generic diagnostics do not have a source location.");
        return Source.GetLocation(Span.Start);
    }

    /// <summary>
    /// Formats the diagnostic code with its severity prefix.
    /// </summary>
    public string FormatCode() => $"{GetSeverityPrefix(Severity)}{DiagnosticMessage.Code:D4}";

    public override string ToString() => $"{FormatCode()}: {Message}";

    /// <summary>
    /// Gets the severity represented by a formatted diagnostic code.
    /// </summary>
    public static DiagnosticSeverity GetSeverity(string code)
    {
        Requires.NotNullOrWhiteSpace(code);
        return code[0] switch
        {
            'W' => DiagnosticSeverity.Warning,
            'I' => DiagnosticSeverity.Note,
            _ => DiagnosticSeverity.Error,
        };
    }

    private static char GetSeverityPrefix(DiagnosticSeverity severity)
    {
        return severity switch
        {
            DiagnosticSeverity.Note => 'I',
            DiagnosticSeverity.Warning => 'W',
            DiagnosticSeverity.Error => 'E',
            _ => Assert.UnreachableValue<char>(), // pragma: force-coverage
        };
    }
}
