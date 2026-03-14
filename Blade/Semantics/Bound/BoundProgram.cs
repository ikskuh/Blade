using System.Collections.Generic;
using Blade;
using Blade.Source;

namespace Blade.Semantics.Bound;

public sealed class BoundProgram : BoundNode
{
    public BoundProgram(
        IReadOnlyList<BoundStatement> topLevelStatements,
        IReadOnlyList<BoundGlobalVariableMember> globalVariables,
        IReadOnlyList<BoundFunctionMember> functions,
        IReadOnlyDictionary<string, TypeSymbol> typeAliases,
        IReadOnlyDictionary<string, FunctionSymbol> functionLookup)
        : base(BoundNodeKind.Program, Requires.NotNull(topLevelStatements).Count > 0
            ? TextSpan.FromBounds(topLevelStatements[0].Span.Start, topLevelStatements[^1].Span.End)
            : new TextSpan(0, 0))
    {
        TopLevelStatements = Requires.NotNull(topLevelStatements);
        GlobalVariables = Requires.NotNull(globalVariables);
        Functions = Requires.NotNull(functions);
        TypeAliases = Requires.NotNull(typeAliases);
        FunctionLookup = Requires.NotNull(functionLookup);
    }

    public IReadOnlyList<BoundStatement> TopLevelStatements { get; }
    public IReadOnlyList<BoundGlobalVariableMember> GlobalVariables { get; }
    public IReadOnlyList<BoundFunctionMember> Functions { get; }
    public IReadOnlyDictionary<string, TypeSymbol> TypeAliases { get; }
    public IReadOnlyDictionary<string, FunctionSymbol> FunctionLookup { get; }
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
