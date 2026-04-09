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
    IReadOnlyList<BoundGlobalVariableMember> globalVariables,
    IReadOnlyList<BoundFunctionMember> functions,
    IReadOnlyDictionary<string, TypeSymbol> typeAliases,
    IReadOnlyDictionary<string, FunctionSymbol> functionLookup,
    IReadOnlyDictionary<string, VariableSymbol> exportedVariables,
    IReadOnlyDictionary<string, BoundModule> importedModules) : BoundNode(BoundNodeKind.Program, Requires.NotNull(topLevelStatements).Count > 0
            ? TextSpan.FromBounds(topLevelStatements[0].Span.Start, topLevelStatements[^1].Span.End)
            : new TextSpan(0, 0))
{
    public string ResolvedFilePath { get; } = Requires.NotNull(resolvedFilePath);
    public CompilationUnitSyntax Syntax { get; } = Requires.NotNull(syntax);
    public IReadOnlyList<BoundStatement> TopLevelStatements { get; } = Requires.NotNull(topLevelStatements);
    public IReadOnlyList<BoundGlobalVariableMember> GlobalVariables { get; } = Requires.NotNull(globalVariables);
    public IReadOnlyList<BoundFunctionMember> Functions { get; } = Requires.NotNull(functions);
    public IReadOnlyDictionary<string, TypeSymbol> TypeAliases { get; } = Requires.NotNull(typeAliases);
    public IReadOnlyDictionary<string, FunctionSymbol> FunctionLookup { get; } = Requires.NotNull(functionLookup);
    public IReadOnlyDictionary<string, VariableSymbol> ExportedVariables { get; } = Requires.NotNull(exportedVariables);
    public IReadOnlyDictionary<string, BoundModule> ImportedModules { get; } = Requires.NotNull(importedModules);
}

public abstract class BoundMember(BoundNodeKind kind, TextSpan span) : BoundNode(kind, span)
{
}

public sealed class BoundGlobalVariableMember(VariableSymbol symbol, BoundExpression? initializer, TextSpan span) : BoundMember(BoundNodeKind.GlobalVariableMember, span)
{
    public VariableSymbol Symbol { get; } = Requires.NotNull(symbol);
    public BoundExpression? Initializer { get; } = initializer;
}

public sealed class BoundFunctionMember(FunctionSymbol symbol, BoundBlockStatement body, TextSpan span) : BoundMember(BoundNodeKind.FunctionMember, span)
{
    public FunctionSymbol Symbol { get; } = Requires.NotNull(symbol);
    public BoundBlockStatement Body { get; } = Requires.NotNull(body);
}
