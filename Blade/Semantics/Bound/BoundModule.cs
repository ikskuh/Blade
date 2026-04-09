using System.Collections.Generic;
using Blade;
using Blade.Semantics;
using Blade.Source;
using Blade.Syntax.Nodes;

namespace Blade.Semantics.Bound;

public sealed class BoundModule : BoundNode
{
    public BoundModule(
        string resolvedFilePath,
        CompilationUnitSyntax syntax,
        IReadOnlyList<BoundStatement> topLevelStatements,
        IReadOnlyList<BoundGlobalVariableMember> globalVariables,
        IReadOnlyList<BoundFunctionMember> functions,
        IReadOnlyDictionary<string, TypeSymbol> typeAliases,
        IReadOnlyDictionary<string, FunctionSymbol> functionLookup,
        IReadOnlyDictionary<string, VariableSymbol> exportedVariables,
        IReadOnlyDictionary<string, BoundModule> importedModules)
        : base(BoundNodeKind.Program, Requires.NotNull(topLevelStatements).Count > 0
            ? TextSpan.FromBounds(topLevelStatements[0].Span.Start, topLevelStatements[^1].Span.End)
            : new TextSpan(0, 0))
    {
        ResolvedFilePath = Requires.NotNull(resolvedFilePath);
        Syntax = Requires.NotNull(syntax);
        TopLevelStatements = Requires.NotNull(topLevelStatements);
        GlobalVariables = Requires.NotNull(globalVariables);
        Functions = Requires.NotNull(functions);
        TypeAliases = Requires.NotNull(typeAliases);
        FunctionLookup = Requires.NotNull(functionLookup);
        ExportedVariables = Requires.NotNull(exportedVariables);
        ImportedModules = Requires.NotNull(importedModules);
    }

    public string ResolvedFilePath { get; }
    public CompilationUnitSyntax Syntax { get; }
    public IReadOnlyList<BoundStatement> TopLevelStatements { get; }
    public IReadOnlyList<BoundGlobalVariableMember> GlobalVariables { get; }
    public IReadOnlyList<BoundFunctionMember> Functions { get; }
    public IReadOnlyDictionary<string, TypeSymbol> TypeAliases { get; }
    public IReadOnlyDictionary<string, FunctionSymbol> FunctionLookup { get; }
    public IReadOnlyDictionary<string, VariableSymbol> ExportedVariables { get; }
    public IReadOnlyDictionary<string, BoundModule> ImportedModules { get; }
}

public abstract class BoundMember : BoundNode
{
    protected BoundMember(BoundNodeKind kind, TextSpan span)
        : base(kind, span)
    {
    }
}

public sealed class BoundGlobalVariableMember : BoundMember
{
    public BoundGlobalVariableMember(VariableSymbol symbol, BoundExpression? initializer, TextSpan span)
        : base(BoundNodeKind.GlobalVariableMember, span)
    {
        Symbol = Requires.NotNull(symbol);
        Initializer = initializer;
    }

    public VariableSymbol Symbol { get; }
    public BoundExpression? Initializer { get; }
}

public sealed class BoundFunctionMember : BoundMember
{
    public BoundFunctionMember(FunctionSymbol symbol, BoundBlockStatement body, TextSpan span)
        : base(BoundNodeKind.FunctionMember, span)
    {
        Symbol = Requires.NotNull(symbol);
        Body = Requires.NotNull(body);
    }

    public FunctionSymbol Symbol { get; }
    public BoundBlockStatement Body { get; }
}
