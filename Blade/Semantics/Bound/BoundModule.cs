using System.Collections.Generic;
using Blade;
using Blade.Semantics;
using Blade.Source;
using Blade.Syntax.Nodes;

namespace Blade.Semantics.Bound;

public sealed class BoundModule(
    string resolvedFilePath,
    CompilationUnitSyntax syntax,
    IReadOnlyList<BoundStatement> topLevelStatements,
    IReadOnlyList<GlobalVariableSymbol> globalVariables,
    IReadOnlyList<BoundFunctionMember> functions,
    IReadOnlyDictionary<string, Symbol> exportedSymbols) : BoundNode(BoundNodeKind.Module, Requires.NotNull(topLevelStatements).Count > 0
            ? TextSpan.FromBounds(topLevelStatements[0].Span.Start, topLevelStatements[^1].Span.End)
            : new TextSpan(0, 0))
{
    public string ResolvedFilePath { get; } = Requires.NotNull(resolvedFilePath);
    public CompilationUnitSyntax Syntax { get; } = Requires.NotNull(syntax);
    public IReadOnlyList<BoundStatement> TopLevelStatements { get; } = Requires.NotNull(topLevelStatements);
    public IReadOnlyList<GlobalVariableSymbol> GlobalVariables { get; } = Requires.NotNull(globalVariables);
    public IReadOnlyList<BoundFunctionMember> Functions { get; } = Requires.NotNull(functions);
    public IReadOnlyDictionary<string, Symbol> ExportedSymbols { get; } = Requires.NotNull(exportedSymbols);
}

public abstract class BoundMember(BoundNodeKind kind, TextSpan span) : BoundNode(kind, span)
{
}

public sealed class BoundFunctionMember(FunctionSymbol symbol, BoundBlockStatement body, TextSpan span) : BoundMember(BoundNodeKind.FunctionMember, span)
{
    public FunctionSymbol Symbol { get; } = Requires.NotNull(symbol);
    public BoundBlockStatement Body { get; } = Requires.NotNull(body);
}
