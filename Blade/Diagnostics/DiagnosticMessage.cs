using Blade;
using Blade.Source;

namespace Blade.Diagnostics;

/// <summary>
/// Base type for generated typed diagnostic messages.
/// </summary>
public abstract partial class DiagnosticMessage(string name, DiagnosticSeverity severity, int code, string message)
{
    /// <summary>
    /// Gets the diagnostic name without severity suffix.
    /// </summary>
    public string Name { get; } = Requires.NotNullOrWhiteSpace(name);

    /// <summary>
    /// Gets the severity of the diagnostic.
    /// </summary>
    public DiagnosticSeverity Severity { get; } = severity;

    /// <summary>
    /// Gets the numeric diagnostic code.
    /// </summary>
    public int Code { get; } = Requires.Positive(code);

    /// <summary>
    /// Gets the formatted human-readable diagnostic text.
    /// </summary>
    public string Message { get; } = Requires.NotNull(message);

    /// <summary>
    /// Gets whether this diagnostic is associated with a source location.
    /// </summary>
    public abstract bool IsLocated { get; }
}

/// <summary>
/// Base type for generated typed diagnostics that point at a source span.
/// </summary>
public abstract class LocatedDiagnosticMessage(
    SourceText source,
    TextSpan span,
    string name,
    DiagnosticSeverity severity,
    int code,
    string message) : DiagnosticMessage(name, severity, code, message)
{
    /// <summary>
    /// Gets the source text that contains the diagnostic span.
    /// </summary>
    public SourceText Source { get; } = Requires.NotNull(source);

    /// <summary>
    /// Gets the source span associated with this diagnostic.
    /// </summary>
    public TextSpan Span { get; } = span;

    /// <summary>
    /// Gets whether this diagnostic is associated with a source location.
    /// </summary>
    public override bool IsLocated => true;
}
