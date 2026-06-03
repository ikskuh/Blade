using System.Collections.Generic;
using Blade;
using Blade.Semantics;
using Blade.Source;
using Blade.Syntax.Nodes;

namespace Blade.Semantics.Bound;

/// <summary>
/// Represents one fully bound source module together with its declarations and exported symbols.
/// </summary>
public sealed class BoundModule(
    string resolvedFilePath,
    CompilationUnitSyntax syntax,
    IReadOnlyList<GlobalVariableSymbol> globalVariables,
    IReadOnlyList<BoundFunctionMember> functions,
    IReadOnlyList<BoundTopLevelAssertMember> topLevelAssertions,
    IReadOnlyDictionary<string, Symbol> exportedSymbols) : BoundNode(BoundNodeKind.Module, Requires.NotNull(syntax).Span)
{
    /// <summary>
    /// Gets the resolved source file path for this module.
    /// </summary>
    public string ResolvedFilePath { get; } = Requires.NotNull(resolvedFilePath);

    /// <summary>
    /// Gets the parsed compilation unit that produced this module.
    /// </summary>
    public CompilationUnitSyntax Syntax { get; } = Requires.NotNull(syntax);

    /// <summary>
    /// Gets the global storage declarations owned by this module.
    /// </summary>
    public IReadOnlyList<GlobalVariableSymbol> GlobalVariables { get; } = Requires.NotNull(globalVariables);

    /// <summary>
    /// Gets the function members declared by this module.
    /// </summary>
    public IReadOnlyList<BoundFunctionMember> Functions { get; } = Requires.NotNull(functions);

    /// <summary>
    /// Gets the compile-time assert members declared at module scope.
    /// </summary>
    public IReadOnlyList<BoundTopLevelAssertMember> TopLevelAssertions { get; } = Requires.NotNull(topLevelAssertions);

    /// <summary>
    /// Gets the exported symbols that may be referenced from importing modules.
    /// </summary>
    public IReadOnlyDictionary<string, Symbol> ExportedSymbols { get; } = Requires.NotNull(exportedSymbols);
}

public abstract class BoundMember(BoundNodeKind kind, TextSpan span) : BoundNode(kind, span)
{
}

/// <summary>
/// Represents one compile-time assertion declared at module scope.
/// </summary>
public sealed class BoundTopLevelAssertMember(BoundExpression condition, string? message, TextSpan span) : BoundMember(BoundNodeKind.TopLevelAssertMember, span)
{
    /// <summary>
    /// Gets the bound boolean condition for the module-level assertion.
    /// </summary>
    public BoundExpression Condition { get; } = Requires.NotNull(condition);

    /// <summary>
    /// Gets the optional message emitted if the assertion fails.
    /// </summary>
    public string? Message { get; } = message;
}

public sealed class BoundFunctionMember(FunctionSymbol symbol, BoundBlockStatement body, TextSpan span) : BoundMember(BoundNodeKind.FunctionMember, span)
{
    public FunctionSymbol Symbol { get; } = Requires.NotNull(symbol);
    public BoundBlockStatement Body { get; } = Requires.NotNull(body);
}
