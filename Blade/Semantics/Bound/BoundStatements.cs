using System.Collections.Generic;
using Blade.Semantics;
using Blade.Source;
using Blade.Syntax;

namespace Blade.Semantics.Bound;

public abstract class BoundStatement(BoundNodeKind kind, TextSpan span) : BoundNode(kind, span)
{
}

public sealed class BoundBlockStatement(IReadOnlyList<BoundStatement> statements, TextSpan span) : BoundStatement(BoundNodeKind.BlockStatement, span)
{
    public IReadOnlyList<BoundStatement> Statements { get; } = statements;
}

public sealed class BoundVariableDeclarationStatement(VariableSymbol symbol, BoundExpression? initializer, TextSpan span) : BoundStatement(BoundNodeKind.VariableDeclarationStatement, span)
{
    public VariableSymbol Symbol { get; } = symbol;
    public BoundExpression? Initializer { get; } = initializer;
}

public sealed class BoundAssignmentStatement(BoundAssignmentTarget target, BoundExpression value, TokenKind operatorKind, TextSpan span) : BoundStatement(BoundNodeKind.AssignmentStatement, span)
{
    public BoundAssignmentTarget Target { get; } = target;
    public BoundExpression Value { get; } = value;
    public TokenKind OperatorKind { get; } = operatorKind;
}

public sealed class BoundMultiAssignmentStatement(IReadOnlyList<BoundAssignmentTarget> targets, BoundCallExpression call, TextSpan span) : BoundStatement(BoundNodeKind.MultiAssignmentStatement, span)
{
    public IReadOnlyList<BoundAssignmentTarget> Targets { get; } = targets;
    public BoundCallExpression Call { get; } = call;
}

public sealed class BoundExpressionStatement(BoundExpression expression, TextSpan span) : BoundStatement(BoundNodeKind.ExpressionStatement, span)
{
    public BoundExpression Expression { get; } = expression;
}

public sealed class BoundIfStatement(BoundExpression condition, BoundStatement thenBody, BoundStatement? elseBody, TextSpan span) : BoundStatement(BoundNodeKind.IfStatement, span)
{
    public BoundExpression Condition { get; } = condition;
    public BoundStatement ThenBody { get; } = thenBody;
    public BoundStatement? ElseBody { get; } = elseBody;
}

public sealed class BoundWhileStatement(BoundExpression condition, BoundBlockStatement body, TextSpan span) : BoundStatement(BoundNodeKind.WhileStatement, span)
{
    public BoundExpression Condition { get; } = condition;
    public BoundBlockStatement Body { get; } = body;
}

public sealed class BoundForStatement(
    BoundExpression iterable,
    VariableSymbol? itemVariable,
    bool itemIsMutable,
    VariableSymbol? indexVariable,
    BoundBlockStatement body,
    TextSpan span) : BoundStatement(BoundNodeKind.ForStatement, span)
{
    public BoundExpression Iterable { get; } = iterable;
    public VariableSymbol? ItemVariable { get; } = itemVariable;
    public bool ItemIsMutable { get; } = itemIsMutable;
    public VariableSymbol? IndexVariable { get; } = indexVariable;
    public BoundBlockStatement Body { get; } = body;
}

public sealed class BoundLoopStatement(BoundBlockStatement body, TextSpan span) : BoundStatement(BoundNodeKind.LoopStatement, span)
{
    public BoundBlockStatement Body { get; } = body;
}

public sealed class BoundRepLoopStatement(BoundBlockStatement body, TextSpan span) : BoundStatement(BoundNodeKind.RepLoopStatement, span)
{
    public BoundBlockStatement Body { get; } = body;
}

public sealed class BoundRepForStatement(VariableSymbol variable, BoundExpression start, BoundExpression end, BoundBlockStatement body, TextSpan span) : BoundStatement(BoundNodeKind.RepForStatement, span)
{
    public VariableSymbol Variable { get; } = variable;
    public BoundExpression Start { get; } = start;
    public BoundExpression End { get; } = end;
    public BoundBlockStatement Body { get; } = body;
}

public sealed class BoundNoirqStatement(BoundBlockStatement body, TextSpan span) : BoundStatement(BoundNodeKind.NoirqStatement, span)
{
    public BoundBlockStatement Body { get; } = body;
}

public sealed class BoundReturnStatement(IReadOnlyList<BoundExpression> values, TextSpan span) : BoundStatement(BoundNodeKind.ReturnStatement, span)
{
    public IReadOnlyList<BoundExpression> Values { get; } = values;
}

public sealed class BoundBreakStatement(TextSpan span) : BoundStatement(BoundNodeKind.BreakStatement, span)
{
}

public sealed class BoundContinueStatement(TextSpan span) : BoundStatement(BoundNodeKind.ContinueStatement, span)
{
}

public sealed class BoundYieldStatement(TextSpan span) : BoundStatement(BoundNodeKind.YieldStatement, span)
{
}

public sealed class BoundYieldtoStatement(FunctionSymbol? target, IReadOnlyList<BoundExpression> arguments, TextSpan span) : BoundStatement(BoundNodeKind.YieldtoStatement, span)
{
    public FunctionSymbol? Target { get; } = target;
    public IReadOnlyList<BoundExpression> Arguments { get; } = arguments;
}

public sealed class BoundAsmStatement(
    AsmVolatility volatility,
    InlineAsmFlagOutput? flagOutput,
    IReadOnlyList<InlineAsmLine> parsedLines,
    IReadOnlyDictionary<InlineAsmBindingSlot, Symbol> referencedSymbols,
    TextSpan span) : BoundStatement(BoundNodeKind.AsmStatement, span)
{
    public AsmVolatility Volatility { get; } = volatility;
    public InlineAsmFlagOutput? FlagOutput { get; } = flagOutput;
    public IReadOnlyList<InlineAsmLine> ParsedLines { get; } = parsedLines;
    public IReadOnlyDictionary<InlineAsmBindingSlot, Symbol> ReferencedSymbols { get; } = referencedSymbols;
}

/// <summary>
/// Only required inside the binder as a sentinel value, not a public api.
/// </summary>
internal sealed class BoundErrorStatement(TextSpan span) : BoundStatement(BoundNodeKind.ErrorStatement, span)
{
}
