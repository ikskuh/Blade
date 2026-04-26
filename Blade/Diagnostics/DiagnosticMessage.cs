using System;
using System.Globalization;
using System.Linq;
using Blade;
using Blade.Source;

namespace Blade.Diagnostics;

/// <summary>
/// Base type for generated typed diagnostic messages.
/// </summary>
public abstract partial class DiagnosticMessage
{
    private readonly string? _message;

    protected DiagnosticMessage(string name, DiagnosticSeverity severity, int code, string message)
        : this(name, severity, code)
    {
        _message = Requires.NotNull(message);
    }

    protected DiagnosticMessage(string name, DiagnosticSeverity severity, int code)
    {
        Name = Requires.NotNullOrWhiteSpace(name);
        Severity = severity;
        Code = Requires.Positive(code);
    }

    /// <summary>
    /// Gets the diagnostic name without severity suffix.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the severity of the diagnostic.
    /// </summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>
    /// Gets the numeric diagnostic code.
    /// </summary>
    public int Code { get; }

    /// <summary>
    /// Gets the formatted human-readable diagnostic text.
    /// </summary>
    public string Message => _message ?? FormatMessage();

    /// <summary>
    /// Gets whether this diagnostic is associated with a source location.
    /// </summary>
    public abstract bool IsLocated { get; }

    protected virtual FormattableString GetFormattableMessage()
    {
        throw new InvalidOperationException($"Diagnostic '{Name}' does not provide a formatted message."); // pragma: force-coverage
    }

    /// <summary>
    /// Converts the diagnostic message into a human-readable version.
    /// </summary>
    public string FormatMessage()
    {
        FormattableString msg = GetFormattableMessage();
        return string.Format(
            CultureInfo.InvariantCulture,
            msg.Format,
            msg.GetArguments().Select(Formatter.Format).ToArray()
        );
    }
}

/// <summary>
/// Base type for generated typed diagnostics that point at a source span.
/// </summary>
public abstract class LocatedDiagnosticMessage : DiagnosticMessage
{
    protected LocatedDiagnosticMessage(
        SourceText source,
        TextSpan span,
        string name,
        DiagnosticSeverity severity,
        int code,
        string message)
        : base(name, severity, code, message)
    {
        Source = Requires.NotNull(source);
        Span = span;
    }

    protected LocatedDiagnosticMessage(
        SourceText source,
        TextSpan span,
        string name,
        DiagnosticSeverity severity,
        int code)
        : base(name, severity, code)
    {
        Source = Requires.NotNull(source);
        Span = span;
    }

    /// <summary>
    /// Gets the source text that contains the diagnostic span.
    /// </summary>
    public SourceText Source { get; }

    /// <summary>
    /// Gets the source span associated with this diagnostic.
    /// </summary>
    public TextSpan Span { get; }

    /// <summary>
    /// Gets whether this diagnostic is associated with a source location.
    /// </summary>
    public override bool IsLocated => true;
}
