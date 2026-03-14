using System.Collections.Generic;
using Blade;
using Blade.Source;

namespace Blade.Syntax.Nodes;

/// <summary>
/// Base class for all expression nodes.
/// </summary>
public abstract class ExpressionSyntax : SyntaxNode
{
    protected ExpressionSyntax(TextSpan span) : base(span) { }
}

public sealed class LiteralExpressionSyntax : ExpressionSyntax
{
    public Token Token { get; }

    public LiteralExpressionSyntax(Token token)
        : base(token.Span)
    {
        Token = token;
    }
}

public sealed class NameExpressionSyntax : ExpressionSyntax
{
    public Token Name { get; }

    public NameExpressionSyntax(Token name)
        : base(name.Span)
    {
        Name = name;
    }
}

public sealed class ParenthesizedExpressionSyntax : ExpressionSyntax
{
    public Token OpenParen { get; }
    public ExpressionSyntax Expression { get; }
    public Token CloseParen { get; }

    public ParenthesizedExpressionSyntax(Token openParen, ExpressionSyntax expression, Token closeParen)
        : base(TextSpan.FromBounds(openParen.Span.Start, Requires.NotNull(expression).Span.End))
    {
        OpenParen = openParen;
        Expression = Requires.NotNull(expression);
        CloseParen = closeParen;
    }
}

public sealed class UnaryExpressionSyntax : ExpressionSyntax
{
    public Token Operator { get; }
    public ExpressionSyntax Operand { get; }

    public UnaryExpressionSyntax(Token @operator, ExpressionSyntax operand)
        : base(TextSpan.FromBounds(@operator.Span.Start, Requires.NotNull(operand).Span.End))
    {
        Operator = @operator;
        Operand = Requires.NotNull(operand);
    }
}

public sealed class BinaryExpressionSyntax : ExpressionSyntax
{
    public ExpressionSyntax Left { get; }
    public Token Operator { get; }
    public ExpressionSyntax Right { get; }

    public BinaryExpressionSyntax(ExpressionSyntax left, Token @operator, ExpressionSyntax right)
        : base(TextSpan.FromBounds(Requires.NotNull(left).Span.Start, Requires.NotNull(right).Span.End))
    {
        Left = Requires.NotNull(left);
        Operator = @operator;
        Right = Requires.NotNull(right);
    }
}

public sealed class PostfixUnaryExpressionSyntax : ExpressionSyntax
{
    public ExpressionSyntax Operand { get; }
    public Token Operator { get; }

    public PostfixUnaryExpressionSyntax(ExpressionSyntax operand, Token @operator)
        : base(TextSpan.FromBounds(Requires.NotNull(operand).Span.Start, @operator.Span.End))
    {
        Operand = Requires.NotNull(operand);
        Operator = @operator;
    }
}

public sealed class MemberAccessExpressionSyntax : ExpressionSyntax
{
    public ExpressionSyntax Expression { get; }
    public Token Dot { get; }
    public Token Member { get; }

    public MemberAccessExpressionSyntax(ExpressionSyntax expression, Token dot, Token member)
        : base(TextSpan.FromBounds(Requires.NotNull(expression).Span.Start, member.Span.End))
    {
        Expression = Requires.NotNull(expression);
        Dot = dot;
        Member = member;
    }
}

public sealed class PointerDerefExpressionSyntax : ExpressionSyntax
{
    public ExpressionSyntax Expression { get; }
    public Token Dot { get; }
    public Token Star { get; }

    public PointerDerefExpressionSyntax(ExpressionSyntax expression, Token dot, Token star)
        : base(TextSpan.FromBounds(Requires.NotNull(expression).Span.Start, star.Span.End))
    {
        Expression = Requires.NotNull(expression);
        Dot = dot;
        Star = star;
    }
}

public sealed class IndexExpressionSyntax : ExpressionSyntax
{
    public ExpressionSyntax Expression { get; }
    public Token OpenBracket { get; }
    public ExpressionSyntax Index { get; }
    public Token CloseBracket { get; }

    public IndexExpressionSyntax(ExpressionSyntax expression, Token openBracket, ExpressionSyntax index, Token closeBracket)
        : base(TextSpan.FromBounds(Requires.NotNull(expression).Span.Start, closeBracket.Span.End))
    {
        Expression = Requires.NotNull(expression);
        OpenBracket = openBracket;
        Index = Requires.NotNull(index);
        CloseBracket = closeBracket;
    }
}

public sealed class CallExpressionSyntax : ExpressionSyntax
{
    public ExpressionSyntax Callee { get; }
    public Token OpenParen { get; }
    public SeparatedSyntaxList<ExpressionSyntax> Arguments { get; }
    public Token CloseParen { get; }

    public CallExpressionSyntax(ExpressionSyntax callee, Token openParen, SeparatedSyntaxList<ExpressionSyntax> arguments, Token closeParen)
        : base(TextSpan.FromBounds(Requires.NotNull(callee).Span.Start, closeParen.Span.End))
    {
        Callee = Requires.NotNull(callee);
        OpenParen = openParen;
        Arguments = arguments;
        CloseParen = closeParen;
    }
}

public sealed class IntrinsicCallExpressionSyntax : ExpressionSyntax
{
    public Token AtToken { get; }
    public Token Name { get; }
    public Token OpenParen { get; }
    public SeparatedSyntaxList<ExpressionSyntax> Arguments { get; }
    public Token CloseParen { get; }

    public IntrinsicCallExpressionSyntax(Token atToken, Token name, Token openParen, SeparatedSyntaxList<ExpressionSyntax> arguments, Token closeParen)
        : base(TextSpan.FromBounds(atToken.Span.Start, closeParen.Span.End))
    {
        AtToken = atToken;
        Name = name;
        OpenParen = openParen;
        Arguments = arguments;
        CloseParen = closeParen;
    }
}

public sealed class StructLiteralExpressionSyntax : ExpressionSyntax
{
    public Token Dot { get; }
    public Token OpenBrace { get; }
    public SeparatedSyntaxList<FieldInitializerSyntax> Initializers { get; }
    public Token CloseBrace { get; }

    public StructLiteralExpressionSyntax(Token dot, Token openBrace, SeparatedSyntaxList<FieldInitializerSyntax> initializers, Token closeBrace)
        : base(TextSpan.FromBounds(dot.Span.Start, closeBrace.Span.End))
    {
        Dot = dot;
        OpenBrace = openBrace;
        Initializers = initializers;
        CloseBrace = closeBrace;
    }
}

public sealed class ComptimeExpressionSyntax : ExpressionSyntax
{
    public Token ComptimeKeyword { get; }
    public BlockStatementSyntax Body { get; }

    public ComptimeExpressionSyntax(Token comptimeKeyword, BlockStatementSyntax body)
        : base(TextSpan.FromBounds(comptimeKeyword.Span.Start, Requires.NotNull(body).Span.End))
    {
        ComptimeKeyword = comptimeKeyword;
        Body = Requires.NotNull(body);
    }
}

public sealed class IfExpressionSyntax : ExpressionSyntax
{
    public Token IfKeyword { get; }
    public Token OpenParen { get; }
    public ExpressionSyntax Condition { get; }
    public Token CloseParen { get; }
    public ExpressionSyntax ThenExpression { get; }
    public Token ElseKeyword { get; }
    public ExpressionSyntax ElseExpression { get; }

    public IfExpressionSyntax(Token ifKeyword, Token openParen, ExpressionSyntax condition, Token closeParen,
                              ExpressionSyntax thenExpression, Token elseKeyword, ExpressionSyntax elseExpression)
        : base(TextSpan.FromBounds(ifKeyword.Span.Start, Requires.NotNull(elseExpression).Span.End))
    {
        IfKeyword = ifKeyword;
        OpenParen = openParen;
        Condition = Requires.NotNull(condition);
        CloseParen = closeParen;
        ThenExpression = Requires.NotNull(thenExpression);
        ElseKeyword = elseKeyword;
        ElseExpression = Requires.NotNull(elseExpression);
    }
}

public sealed class RangeExpressionSyntax : ExpressionSyntax
{
    public ExpressionSyntax Start { get; }
    public Token DotDot { get; }
    public ExpressionSyntax End { get; }

    public RangeExpressionSyntax(ExpressionSyntax start, Token dotDot, ExpressionSyntax end)
        : base(TextSpan.FromBounds(Requires.NotNull(start).Span.Start, Requires.NotNull(end).Span.End))
    {
        Start = Requires.NotNull(start);
        DotDot = dotDot;
        End = Requires.NotNull(end);
    }
}
