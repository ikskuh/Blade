using System.Collections.Generic;
using Blade;
using Blade.Semantics;
using Blade.Source;
using Blade.Syntax.Nodes;

namespace Blade.Semantics.Bound;

public sealed class BoundModule(
    string resolvedFilePath,
    CompilationUnitSyntax syntax,
    BoundFunctionMember constructor,
    IReadOnlyList<GlobalVariableSymbol> globalVariables,
    IReadOnlyList<BoundFunctionMember> functions,
    IReadOnlyDictionary<string, Symbol> exportedSymbols) : BoundNode(BoundNodeKind.Module, Requires.NotNull(constructor).Body.Span)
{
    public string ResolvedFilePath { get; } = Requires.NotNull(resolvedFilePath);
    public CompilationUnitSyntax Syntax { get; } = Requires.NotNull(syntax);
    public BoundFunctionMember Constructor { get; } = Requires.NotNull(constructor);
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
