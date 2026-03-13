using System.Collections.Generic;
using Blade.Semantics;
using Blade.Source;
using Blade.Syntax;

namespace Blade.Semantics.Bound;

public abstract class BoundStatement : BoundNode
{
    protected BoundStatement(BoundNodeKind kind, TextSpan span)
        : base(kind, span)
    {
    }
}

public sealed class BoundBlockStatement : BoundStatement
{
    public BoundBlockStatement(IReadOnlyList<BoundStatement> statements, TextSpan span)
        : base(BoundNodeKind.BlockStatement, span)
    {
        Statements = statements;
    }

    public IReadOnlyList<BoundStatement> Statements { get; }
}

public sealed class BoundVariableDeclarationStatement : BoundStatement
{
    public BoundVariableDeclarationStatement(VariableSymbol symbol, BoundExpression? initializer, TextSpan span)
        : base(BoundNodeKind.VariableDeclarationStatement, span)
    {
        Symbol = symbol;
        Initializer = initializer;
    }

    public VariableSymbol Symbol { get; }
    public BoundExpression? Initializer { get; }
}

public sealed class BoundAssignmentStatement : BoundStatement
{
    public BoundAssignmentStatement(BoundAssignmentTarget target, BoundExpression value, TokenKind operatorKind, TextSpan span)
        : base(BoundNodeKind.AssignmentStatement, span)
    {
        Target = target;
        Value = value;
        OperatorKind = operatorKind;
    }

    public BoundAssignmentTarget Target { get; }
    public BoundExpression Value { get; }
    public TokenKind OperatorKind { get; }
}

public sealed class BoundExpressionStatement : BoundStatement
{
    public BoundExpressionStatement(BoundExpression expression, TextSpan span)
        : base(BoundNodeKind.ExpressionStatement, span)
    {
        Expression = expression;
    }

    public BoundExpression Expression { get; }
}

public sealed class BoundIfStatement : BoundStatement
{
    public BoundIfStatement(BoundExpression condition, BoundStatement thenBody, BoundStatement? elseBody, TextSpan span)
        : base(BoundNodeKind.IfStatement, span)
    {
        Condition = condition;
        ThenBody = thenBody;
        ElseBody = elseBody;
    }

    public BoundExpression Condition { get; }
    public BoundStatement ThenBody { get; }
    public BoundStatement? ElseBody { get; }
}

public sealed class BoundWhileStatement : BoundStatement
{
    public BoundWhileStatement(BoundExpression condition, BoundBlockStatement body, TextSpan span)
        : base(BoundNodeKind.WhileStatement, span)
    {
        Condition = condition;
        Body = body;
    }

    public BoundExpression Condition { get; }
    public BoundBlockStatement Body { get; }
}

public sealed class BoundForStatement : BoundStatement
{
    public BoundForStatement(Symbol? variable, BoundBlockStatement body, TextSpan span)
        : base(BoundNodeKind.ForStatement, span)
    {
        Variable = variable;
        Body = body;
    }

    public Symbol? Variable { get; }
    public BoundBlockStatement Body { get; }
}

public sealed class BoundLoopStatement : BoundStatement
{
    public BoundLoopStatement(BoundBlockStatement body, TextSpan span)
        : base(BoundNodeKind.LoopStatement, span)
    {
        Body = body;
    }

    public BoundBlockStatement Body { get; }
}

public sealed class BoundRepLoopStatement : BoundStatement
{
    public BoundRepLoopStatement(BoundExpression count, BoundBlockStatement body, TextSpan span)
        : base(BoundNodeKind.RepLoopStatement, span)
    {
        Count = count;
        Body = body;
    }

    public BoundExpression Count { get; }
    public BoundBlockStatement Body { get; }
}

public sealed class BoundRepForStatement : BoundStatement
{
    public BoundRepForStatement(VariableSymbol variable, BoundExpression start, BoundExpression end, BoundBlockStatement body, TextSpan span)
        : base(BoundNodeKind.RepForStatement, span)
    {
        Variable = variable;
        Start = start;
        End = end;
        Body = body;
    }

    public VariableSymbol Variable { get; }
    public BoundExpression Start { get; }
    public BoundExpression End { get; }
    public BoundBlockStatement Body { get; }
}

public sealed class BoundNoirqStatement : BoundStatement
{
    public BoundNoirqStatement(BoundBlockStatement body, TextSpan span)
        : base(BoundNodeKind.NoirqStatement, span)
    {
        Body = body;
    }

    public BoundBlockStatement Body { get; }
}

public sealed class BoundReturnStatement : BoundStatement
{
    public BoundReturnStatement(IReadOnlyList<BoundExpression> values, TextSpan span)
        : base(BoundNodeKind.ReturnStatement, span)
    {
        Values = values;
    }

    public IReadOnlyList<BoundExpression> Values { get; }
}

public sealed class BoundBreakStatement : BoundStatement
{
    public BoundBreakStatement(TextSpan span)
        : base(BoundNodeKind.BreakStatement, span)
    {
    }
}

public sealed class BoundContinueStatement : BoundStatement
{
    public BoundContinueStatement(TextSpan span)
        : base(BoundNodeKind.ContinueStatement, span)
    {
    }
}

public sealed class BoundYieldStatement : BoundStatement
{
    public BoundYieldStatement(TextSpan span)
        : base(BoundNodeKind.YieldStatement, span)
    {
    }
}

public sealed class BoundYieldtoStatement : BoundStatement
{
    public BoundYieldtoStatement(FunctionSymbol? target, IReadOnlyList<BoundExpression> arguments, TextSpan span)
        : base(BoundNodeKind.YieldtoStatement, span)
    {
        Target = target;
        Arguments = arguments;
    }

    public FunctionSymbol? Target { get; }
    public IReadOnlyList<BoundExpression> Arguments { get; }
}

public sealed class BoundAsmStatement : BoundStatement
{
    public BoundAsmStatement(
        string body,
        string? flagOutput,
        IReadOnlyList<InlineAssemblyValidator.AsmLine> parsedLines,
        IReadOnlyDictionary<string, Symbol> referencedSymbols,
        TextSpan span)
        : base(BoundNodeKind.AsmStatement, span)
    {
        Body = body;
        FlagOutput = flagOutput;
        ParsedLines = parsedLines;
        ReferencedSymbols = referencedSymbols;
    }

    public string Body { get; }
    public string? FlagOutput { get; }
    public IReadOnlyList<InlineAssemblyValidator.AsmLine> ParsedLines { get; }
    public IReadOnlyDictionary<string, Symbol> ReferencedSymbols { get; }
}

public sealed class BoundErrorStatement : BoundStatement
{
    public BoundErrorStatement(TextSpan span)
        : base(BoundNodeKind.ErrorStatement, span)
    {
    }
}
