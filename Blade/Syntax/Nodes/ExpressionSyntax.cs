using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; }
    public ExpressionSyntax Expression { get; }
    [ExcludeFromCodeCoverage]
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
    [ExcludeFromCodeCoverage]
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
    [ExcludeFromCodeCoverage] public Token Dot { get; }
    [ExcludeFromCodeCoverage] public Token Star { get; }

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
    [ExcludeFromCodeCoverage]
    public Token OpenBracket { get; }
    public ExpressionSyntax Index { get; }
    [ExcludeFromCodeCoverage]
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
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; }
    public SeparatedSyntaxList<ExpressionSyntax> Arguments { get; }
    [ExcludeFromCodeCoverage]
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
    [ExcludeFromCodeCoverage]
    public Token AtToken { get; }
    public Token Name { get; }
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; }
    public SeparatedSyntaxList<ExpressionSyntax> Arguments { get; }
    [ExcludeFromCodeCoverage]
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

public sealed class IfExpressionSyntax : ExpressionSyntax
{
    [ExcludeFromCodeCoverage]
    public Token IfKeyword { get; }
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; }
    public ExpressionSyntax Condition { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; }
    public ExpressionSyntax ThenExpression { get; }
    [ExcludeFromCodeCoverage]
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

public sealed class CastExpressionSyntax : ExpressionSyntax
{
    public ExpressionSyntax Expression { get; }
    [ExcludeFromCodeCoverage]
    public Token AsKeyword { get; }
    public TypeSyntax TargetType { get; }

    public CastExpressionSyntax(ExpressionSyntax expression, Token asKeyword, TypeSyntax targetType)
        : base(TextSpan.FromBounds(Requires.NotNull(expression).Span.Start, Requires.NotNull(targetType).Span.End))
    {
        Expression = Requires.NotNull(expression);
        AsKeyword = asKeyword;
        TargetType = Requires.NotNull(targetType);
    }
}

public sealed class BitcastExpressionSyntax : ExpressionSyntax
{
    [ExcludeFromCodeCoverage]
    public Token BitcastKeyword { get; }
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; }
    public TypeSyntax TargetType { get; }
    public Token Comma { get; }
    public ExpressionSyntax Value { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; }

    public BitcastExpressionSyntax(Token bitcastKeyword, Token openParen, TypeSyntax targetType,
                                   Token comma, ExpressionSyntax value, Token closeParen)
        : base(TextSpan.FromBounds(bitcastKeyword.Span.Start, closeParen.Span.End))
    {
        BitcastKeyword = bitcastKeyword;
        OpenParen = openParen;
        TargetType = Requires.NotNull(targetType);
        Comma = comma;
        Value = Requires.NotNull(value);
        CloseParen = closeParen;
    }
}

public sealed class QueryExpressionSyntax : ExpressionSyntax
{
    public Token Keyword { get; }
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; }
    public TypeSyntax Subject { get; }
    public Token? Comma { get; }
    public ExpressionSyntax? MemorySpace { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; }

    public QueryExpressionSyntax(Token keyword, Token openParen, TypeSyntax subject,
                                  Token? comma, ExpressionSyntax? memorySpace, Token closeParen)
        : base(TextSpan.FromBounds(keyword.Span.Start, closeParen.Span.End))
    {
        Keyword = keyword;
        OpenParen = openParen;
        Subject = Requires.NotNull(subject);
        Comma = comma;
        MemorySpace = memorySpace;
        CloseParen = closeParen;
    }

    public bool IsTwoArgumentForm => Comma is not null;
}

public sealed class EnumLiteralExpressionSyntax : ExpressionSyntax
{
    [ExcludeFromCodeCoverage]    
    public Token Dot { get; }
    public Token MemberName { get; }

    public EnumLiteralExpressionSyntax(Token dot, Token memberName)
        : base(TextSpan.FromBounds(dot.Span.Start, memberName.Span.End))
    {
        Dot = dot;
        MemberName = memberName;
    }
}

public sealed class ArrayLiteralExpressionSyntax : ExpressionSyntax
{
    [ExcludeFromCodeCoverage]
    public Token OpenBracket { get; }
    public SeparatedSyntaxList<ArrayElementSyntax> Elements { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseBracket { get; }

    public ArrayLiteralExpressionSyntax(Token openBracket, SeparatedSyntaxList<ArrayElementSyntax> elements,
                                        Token closeBracket)
        : base(TextSpan.FromBounds(openBracket.Span.Start, closeBracket.Span.End))
    {
        OpenBracket = openBracket;
        Elements = elements;
        CloseBracket = closeBracket;
    }
}

public sealed class ArrayElementSyntax : SyntaxNode
{
    public ExpressionSyntax Value { get; }
    public Token? Spread { get; }

    public ArrayElementSyntax(ExpressionSyntax value, Token? spread)
        : base(TextSpan.FromBounds(Requires.NotNull(value).Span.Start,
            spread?.Span.End ?? Requires.NotNull(value).Span.End))
    {
        Value = Requires.NotNull(value);
        Spread = spread;
    }
}

public sealed class TypedStructLiteralExpressionSyntax : ExpressionSyntax
{
    public ExpressionSyntax TypeName { get; }
    [ExcludeFromCodeCoverage]
    public Token OpenBrace { get; }
    public SeparatedSyntaxList<FieldInitializerSyntax> Initializers { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseBrace { get; }

    public TypedStructLiteralExpressionSyntax(ExpressionSyntax typeName, Token openBrace,
                                              SeparatedSyntaxList<FieldInitializerSyntax> initializers,
                                              Token closeBrace)
        : base(TextSpan.FromBounds(Requires.NotNull(typeName).Span.Start, closeBrace.Span.End))
    {
        TypeName = Requires.NotNull(typeName);
        OpenBrace = openBrace;
        Initializers = initializers;
        CloseBrace = closeBrace;
    }
}

public sealed class RangeExpressionSyntax : ExpressionSyntax
{
    public ExpressionSyntax Start { get; }
    [ExcludeFromCodeCoverage]    
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
