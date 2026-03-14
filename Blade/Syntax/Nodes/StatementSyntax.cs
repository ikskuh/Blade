using System.Collections.Generic;
using Blade.Source;

namespace Blade.Syntax.Nodes;

/// <summary>
/// Base class for all statement nodes.
/// </summary>
public abstract class StatementSyntax : SyntaxNode
{
    protected StatementSyntax(TextSpan span) : base(span) { }
}

public sealed class BlockStatementSyntax : StatementSyntax
{
    public Token OpenBrace { get; }
    public IReadOnlyList<StatementSyntax> Statements { get; }
    public Token CloseBrace { get; }

    public BlockStatementSyntax(Token openBrace, IReadOnlyList<StatementSyntax> statements, Token closeBrace)
        : base(TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End))
    {
        OpenBrace = openBrace;
        Statements = statements;
        CloseBrace = closeBrace;
    }
}

public sealed class VariableDeclarationStatementSyntax : StatementSyntax
{
    public VariableDeclarationSyntax Declaration { get; }

    public VariableDeclarationStatementSyntax(VariableDeclarationSyntax declaration)
        : base(declaration.Span)
    {
        Declaration = declaration;
    }
}

public sealed class ExpressionStatementSyntax : StatementSyntax
{
    public ExpressionSyntax Expression { get; }
    public Token Semicolon { get; }

    public ExpressionStatementSyntax(ExpressionSyntax expression, Token semicolon)
        : base(TextSpan.FromBounds(expression.Span.Start, semicolon.Span.End))
    {
        Expression = expression;
        Semicolon = semicolon;
    }
}

public sealed class AssignmentStatementSyntax : StatementSyntax
{
    public ExpressionSyntax Target { get; }
    public Token Operator { get; }
    public ExpressionSyntax Value { get; }
    public Token Semicolon { get; }

    public AssignmentStatementSyntax(ExpressionSyntax target, Token @operator, ExpressionSyntax value, Token semicolon)
        : base(TextSpan.FromBounds(target.Span.Start, semicolon.Span.End))
    {
        Target = target;
        Operator = @operator;
        Value = value;
        Semicolon = semicolon;
    }
}

public sealed class IfStatementSyntax : StatementSyntax
{
    public Token IfKeyword { get; }
    public Token OpenParen { get; }
    public ExpressionSyntax Condition { get; }
    public Token CloseParen { get; }
    public StatementSyntax ThenBody { get; }
    public ElseClauseSyntax? ElseClause { get; }

    public IfStatementSyntax(Token ifKeyword, Token openParen, ExpressionSyntax condition, Token closeParen,
                             StatementSyntax thenBody, ElseClauseSyntax? elseClause)
        : base(TextSpan.FromBounds(ifKeyword.Span.Start, elseClause?.Span.End ?? thenBody.Span.End))
    {
        IfKeyword = ifKeyword;
        OpenParen = openParen;
        Condition = condition;
        CloseParen = closeParen;
        ThenBody = thenBody;
        ElseClause = elseClause;
    }
}

public sealed class WhileStatementSyntax : StatementSyntax
{
    public Token WhileKeyword { get; }
    public Token OpenParen { get; }
    public ExpressionSyntax Condition { get; }
    public Token CloseParen { get; }
    public BlockStatementSyntax Body { get; }

    public WhileStatementSyntax(Token whileKeyword, Token openParen, ExpressionSyntax condition, Token closeParen, BlockStatementSyntax body)
        : base(TextSpan.FromBounds(whileKeyword.Span.Start, body.Span.End))
    {
        WhileKeyword = whileKeyword;
        OpenParen = openParen;
        Condition = condition;
        CloseParen = closeParen;
        Body = body;
    }
}

public sealed class ForStatementSyntax : StatementSyntax
{
    public Token ForKeyword { get; }
    public Token OpenParen { get; }
    public Token Variable { get; }
    public Token CloseParen { get; }
    public BlockStatementSyntax Body { get; }

    public ForStatementSyntax(Token forKeyword, Token openParen, Token variable, Token closeParen, BlockStatementSyntax body)
        : base(TextSpan.FromBounds(forKeyword.Span.Start, body.Span.End))
    {
        ForKeyword = forKeyword;
        OpenParen = openParen;
        Variable = variable;
        CloseParen = closeParen;
        Body = body;
    }
}

public sealed class LoopStatementSyntax : StatementSyntax
{
    public Token LoopKeyword { get; }
    public BlockStatementSyntax Body { get; }

    public LoopStatementSyntax(Token loopKeyword, BlockStatementSyntax body)
        : base(TextSpan.FromBounds(loopKeyword.Span.Start, body.Span.End))
    {
        LoopKeyword = loopKeyword;
        Body = body;
    }
}

public sealed class RepLoopStatementSyntax : StatementSyntax
{
    public Token RepKeyword { get; }
    public Token LoopKeyword { get; }
    public Token OpenParen { get; }
    public ExpressionSyntax Count { get; }
    public Token CloseParen { get; }
    public BlockStatementSyntax Body { get; }

    public RepLoopStatementSyntax(Token repKeyword, Token loopKeyword, Token openParen, ExpressionSyntax count, Token closeParen, BlockStatementSyntax body)
        : base(TextSpan.FromBounds(repKeyword.Span.Start, body.Span.End))
    {
        RepKeyword = repKeyword;
        LoopKeyword = loopKeyword;
        OpenParen = openParen;
        Count = count;
        CloseParen = closeParen;
        Body = body;
    }
}

public sealed class RepForStatementSyntax : StatementSyntax
{
    public Token RepKeyword { get; }
    public Token ForKeyword { get; }
    public Token OpenParen { get; }
    public Token Variable { get; }
    public Token InKeyword { get; }
    public RangeExpressionSyntax Range { get; }
    public Token CloseParen { get; }
    public BlockStatementSyntax Body { get; }

    public RepForStatementSyntax(Token repKeyword, Token forKeyword, Token openParen, Token variable, Token inKeyword,
                                  RangeExpressionSyntax range, Token closeParen, BlockStatementSyntax body)
        : base(TextSpan.FromBounds(repKeyword.Span.Start, body.Span.End))
    {
        RepKeyword = repKeyword;
        ForKeyword = forKeyword;
        OpenParen = openParen;
        Variable = variable;
        InKeyword = inKeyword;
        Range = range;
        CloseParen = closeParen;
        Body = body;
    }
}

public sealed class NoirqStatementSyntax : StatementSyntax
{
    public Token NoirqKeyword { get; }
    public BlockStatementSyntax Body { get; }

    public NoirqStatementSyntax(Token noirqKeyword, BlockStatementSyntax body)
        : base(TextSpan.FromBounds(noirqKeyword.Span.Start, body.Span.End))
    {
        NoirqKeyword = noirqKeyword;
        Body = body;
    }
}

public sealed class ReturnStatementSyntax : StatementSyntax
{
    public Token ReturnKeyword { get; }
    public SeparatedSyntaxList<ExpressionSyntax>? Values { get; }
    public Token Semicolon { get; }

    public ReturnStatementSyntax(Token returnKeyword, SeparatedSyntaxList<ExpressionSyntax>? values, Token semicolon)
        : base(TextSpan.FromBounds(returnKeyword.Span.Start, semicolon.Span.End))
    {
        ReturnKeyword = returnKeyword;
        Values = values;
        Semicolon = semicolon;
    }
}

public sealed class BreakStatementSyntax : StatementSyntax
{
    public Token BreakKeyword { get; }
    public Token Semicolon { get; }

    public BreakStatementSyntax(Token breakKeyword, Token semicolon)
        : base(TextSpan.FromBounds(breakKeyword.Span.Start, semicolon.Span.End))
    {
        BreakKeyword = breakKeyword;
        Semicolon = semicolon;
    }
}

public sealed class ContinueStatementSyntax : StatementSyntax
{
    public Token ContinueKeyword { get; }
    public Token Semicolon { get; }

    public ContinueStatementSyntax(Token continueKeyword, Token semicolon)
        : base(TextSpan.FromBounds(continueKeyword.Span.Start, semicolon.Span.End))
    {
        ContinueKeyword = continueKeyword;
        Semicolon = semicolon;
    }
}

public sealed class YieldStatementSyntax : StatementSyntax
{
    public Token YieldKeyword { get; }
    public Token Semicolon { get; }

    public YieldStatementSyntax(Token yieldKeyword, Token semicolon)
        : base(TextSpan.FromBounds(yieldKeyword.Span.Start, semicolon.Span.End))
    {
        YieldKeyword = yieldKeyword;
        Semicolon = semicolon;
    }
}

public sealed class YieldtoStatementSyntax : StatementSyntax
{
    public Token YieldtoKeyword { get; }
    public Token Target { get; }
    public Token OpenParen { get; }
    public SeparatedSyntaxList<ExpressionSyntax> Arguments { get; }
    public Token CloseParen { get; }
    public Token Semicolon { get; }

    public YieldtoStatementSyntax(Token yieldtoKeyword, Token target, Token openParen,
                                   SeparatedSyntaxList<ExpressionSyntax> arguments, Token closeParen, Token semicolon)
        : base(TextSpan.FromBounds(yieldtoKeyword.Span.Start, semicolon.Span.End))
    {
        YieldtoKeyword = yieldtoKeyword;
        Target = target;
        OpenParen = openParen;
        Arguments = arguments;
        CloseParen = closeParen;
        Semicolon = semicolon;
    }
}

public sealed class AsmBlockStatementSyntax : StatementSyntax
{
    public Token AsmKeyword { get; }
    public Token? VolatileKeyword { get; }
    public AsmFlagOutputSyntax? FlagOutput { get; }
    public Token OpenBrace { get; }
    public string Body { get; }
    public Token CloseBrace { get; }
    public Token Semicolon { get; }
    public AsmVolatility Volatility => VolatileKeyword is null ? AsmVolatility.NonVolatile : AsmVolatility.Volatile;

    public AsmBlockStatementSyntax(
        Token asmKeyword,
        Token? volatileKeyword,
        AsmFlagOutputSyntax? flagOutput,
        Token openBrace,
        string body,
        Token closeBrace,
        Token semicolon)
        : base(TextSpan.FromBounds(asmKeyword.Span.Start, semicolon.Span.End))
    {
        AsmKeyword = asmKeyword;
        VolatileKeyword = volatileKeyword;
        FlagOutput = flagOutput;
        OpenBrace = openBrace;
        Body = body;
        CloseBrace = closeBrace;
        Semicolon = semicolon;
    }
}
