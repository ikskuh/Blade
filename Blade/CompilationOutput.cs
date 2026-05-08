using System;
using System.Collections.Generic;
using Blade.Diagnostics;
using Blade.IR;
using Blade.Semantics.Bound;
using Blade.Source;
using Blade.Syntax.Nodes;

namespace Blade;

/// <summary>
/// Describes the final state of a compilation attempt.
/// </summary>
public enum CompilationStatus
{
    /// <summary>
    /// The compilation completed successfully and produced final assembly.
    /// </summary>
    Succeeded,

    /// <summary>
    /// The compilation completed with diagnostics and stopped before final assembly.
    /// </summary>
    Failed,

    /// <summary>
    /// The compiler crashed unexpectedly while compiling.
    /// </summary>
    Crashed,
}

/// <summary>
/// Describes an unexpected compiler crash captured in a compilation output.
/// </summary>
public sealed class CompilationCrashInfo(string exceptionType, string message, string? stackTrace)
{
    /// <summary>
    /// Gets the fully-qualified exception type name.
    /// </summary>
    public string ExceptionType { get; } = Requires.NotNullOrWhiteSpace(exceptionType);

    /// <summary>
    /// Gets the exception message.
    /// </summary>
    public string Message { get; } = Requires.NotNull(message);

    /// <summary>
    /// Gets the captured stack trace text, when available.
    /// </summary>
    public string? StackTrace { get; } = stackTrace;
}

/// <summary>
/// Represents the compiler-visible output of one compilation attempt.
/// </summary>
public sealed class CompilationOutput(
    SourceText source,
    CompilationUnitSyntax syntax,
    BoundProgram? boundProgram,
    CompilationStageOutput stages,
    IReadOnlyList<Diagnostic> diagnostics,
    int tokenCount,
    CompilationStatus status,
    CompilationCrashInfo? crash)
{
    /// <summary>
    /// Gets the source text compiled by this invocation.
    /// </summary>
    public SourceText Source { get; } = Requires.NotNull(source);

    /// <summary>
    /// Gets the parsed syntax tree for the root module.
    /// </summary>
    public CompilationUnitSyntax Syntax { get; } = Requires.NotNull(syntax);

    /// <summary>
    /// Gets the bound program when binding completed successfully.
    /// </summary>
    public BoundProgram? BoundProgram { get; } = boundProgram;

    /// <summary>
    /// Gets the partially or fully produced backend stage outputs.
    /// </summary>
    public CompilationStageOutput Stages { get; } = Requires.NotNull(stages);

    /// <summary>
    /// Gets the diagnostics emitted during compilation.
    /// </summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = Requires.NotNull(diagnostics);

    /// <summary>
    /// Gets the lexer token count for the root module.
    /// </summary>
    public int TokenCount { get; } = Requires.NonNegative(tokenCount);

    /// <summary>
    /// Gets the overall completion status.
    /// </summary>
    public CompilationStatus Status { get; } = status;

    /// <summary>
    /// Gets the captured crash information for unexpected compiler failures.
    /// </summary>
    public CompilationCrashInfo? Crash { get; } = crash;

    /// <summary>
    /// Gets or sets the compilation metrics computed for this output.
    /// </summary>
    public CompilationMetrics Metrics { get; set; } = CompilationMetrics.Empty;
}
