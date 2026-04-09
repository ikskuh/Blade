using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Blade;
using Blade.Source;

namespace Blade.Syntax.Nodes;

/// <summary>
/// Base class for all expression nodes.
/// </summary>
public abstract class ExpressionSyntax(TextSpan span) : SyntaxNode(span)
{
}

public sealed class LiteralExpressionSyntax(Token token) : ExpressionSyntax(token.Span)
{
    public Token Token { get; } = token;
}

public sealed class NameExpressionSyntax(Token name) : ExpressionSyntax(name.Span)
{
    public Token Name { get; } = name;
}

public sealed class ParenthesizedExpressionSyntax(Token openParen, ExpressionSyntax expression, Token closeParen) : ExpressionSyntax(TextSpan.FromBounds(openParen.Span.Start, Requires.NotNull(expression).Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; } = openParen;
    public ExpressionSyntax Expression { get; } = Requires.NotNull(expression);
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; } = closeParen;
}

public sealed class UnaryExpressionSyntax(Token @operator, ExpressionSyntax operand) : ExpressionSyntax(TextSpan.FromBounds(@operator.Span.Start, Requires.NotNull(operand).Span.End))
{
    public Token Operator { get; } = @operator;
    public ExpressionSyntax Operand { get; } = Requires.NotNull(operand);
}

public sealed class BinaryExpressionSyntax(ExpressionSyntax left, Token @operator, ExpressionSyntax right) : ExpressionSyntax(TextSpan.FromBounds(Requires.NotNull(left).Span.Start, Requires.NotNull(right).Span.End))
{
    public ExpressionSyntax Left { get; } = Requires.NotNull(left);
    public Token Operator { get; } = @operator;
    public ExpressionSyntax Right { get; } = Requires.NotNull(right);
}


public sealed class MemberAccessExpressionSyntax(ExpressionSyntax expression, Token dot, Token member) : ExpressionSyntax(TextSpan.FromBounds(Requires.NotNull(expression).Span.Start, member.Span.End))
{
    public ExpressionSyntax Expression { get; } = Requires.NotNull(expression);
    [ExcludeFromCodeCoverage]
    public Token Dot { get; } = dot;
    public Token Member { get; } = member;
}

public sealed class PointerDerefExpressionSyntax(ExpressionSyntax expression, Token dot, Token star) : ExpressionSyntax(TextSpan.FromBounds(Requires.NotNull(expression).Span.Start, star.Span.End))
{
    public ExpressionSyntax Expression { get; } = Requires.NotNull(expression);
    [ExcludeFromCodeCoverage] public Token Dot { get; } = dot;
    [ExcludeFromCodeCoverage] public Token Star { get; } = star;
}

public sealed class IndexExpressionSyntax(ExpressionSyntax expression, Token openBracket, ExpressionSyntax index, Token closeBracket) : ExpressionSyntax(TextSpan.FromBounds(Requires.NotNull(expression).Span.Start, closeBracket.Span.End))
{
    public ExpressionSyntax Expression { get; } = Requires.NotNull(expression);
    [ExcludeFromCodeCoverage]
    public Token OpenBracket { get; } = openBracket;
    public ExpressionSyntax Index { get; } = Requires.NotNull(index);
    [ExcludeFromCodeCoverage]
    public Token CloseBracket { get; } = closeBracket;
}

public sealed class CallExpressionSyntax(ExpressionSyntax callee, Token openParen, SeparatedSyntaxList<ExpressionSyntax> arguments, Token closeParen) : ExpressionSyntax(TextSpan.FromBounds(Requires.NotNull(callee).Span.Start, closeParen.Span.End))
{
    public ExpressionSyntax Callee { get; } = Requires.NotNull(callee);
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; } = openParen;
    public SeparatedSyntaxList<ExpressionSyntax> Arguments { get; } = arguments;
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; } = closeParen;
}

public sealed class IntrinsicCallExpressionSyntax(Token atToken, Token name, Token openParen, SeparatedSyntaxList<ExpressionSyntax> arguments, Token closeParen) : ExpressionSyntax(TextSpan.FromBounds(atToken.Span.Start, closeParen.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token AtToken { get; } = atToken;
    public Token Name { get; } = name;
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; } = openParen;
    public SeparatedSyntaxList<ExpressionSyntax> Arguments { get; } = arguments;
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; } = closeParen;
}

public sealed class IfExpressionSyntax(Token ifKeyword, Token openParen, ExpressionSyntax condition, Token closeParen,
                          ExpressionSyntax thenExpression, Token elseKeyword, ExpressionSyntax elseExpression) : ExpressionSyntax(TextSpan.FromBounds(ifKeyword.Span.Start, Requires.NotNull(elseExpression).Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token IfKeyword { get; } = ifKeyword;
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; } = openParen;
    public ExpressionSyntax Condition { get; } = Requires.NotNull(condition);
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; } = closeParen;
    public ExpressionSyntax ThenExpression { get; } = Requires.NotNull(thenExpression);
    [ExcludeFromCodeCoverage]
    public Token ElseKeyword { get; } = elseKeyword;
    public ExpressionSyntax ElseExpression { get; } = Requires.NotNull(elseExpression);
}

public sealed class CastExpressionSyntax(ExpressionSyntax expression, Token asKeyword, TypeSyntax targetType) : ExpressionSyntax(TextSpan.FromBounds(Requires.NotNull(expression).Span.Start, Requires.NotNull(targetType).Span.End))
{
    public ExpressionSyntax Expression { get; } = Requires.NotNull(expression);
    [ExcludeFromCodeCoverage]
    public Token AsKeyword { get; } = asKeyword;
    public TypeSyntax TargetType { get; } = Requires.NotNull(targetType);
}

public sealed class BitcastExpressionSyntax(Token bitcastKeyword, Token openParen, TypeSyntax targetType,
                               Token comma, ExpressionSyntax value, Token closeParen) : ExpressionSyntax(TextSpan.FromBounds(bitcastKeyword.Span.Start, closeParen.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token BitcastKeyword { get; } = bitcastKeyword;
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; } = openParen;
    public TypeSyntax TargetType { get; } = Requires.NotNull(targetType);
    public Token Comma { get; } = comma;
    public ExpressionSyntax Value { get; } = Requires.NotNull(value);
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; } = closeParen;
}

public sealed class QueryExpressionSyntax(Token keyword, Token openParen, TypeSyntax subject,
                              Token? comma, ExpressionSyntax? memorySpace, Token closeParen) : ExpressionSyntax(TextSpan.FromBounds(keyword.Span.Start, closeParen.Span.End))
{
    public Token Keyword { get; } = keyword;
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; } = openParen;
    public TypeSyntax Subject { get; } = Requires.NotNull(subject);
    public Token? Comma { get; } = comma;
    public ExpressionSyntax? MemorySpace { get; } = memorySpace;
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; } = closeParen;

    public bool IsTwoArgumentForm => Comma is not null;
}

public sealed class EnumLiteralExpressionSyntax(Token dot, Token memberName) : ExpressionSyntax(TextSpan.FromBounds(dot.Span.Start, memberName.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token Dot { get; } = dot;
    public Token MemberName { get; } = memberName;
}

public sealed class ArrayLiteralExpressionSyntax(Token openBracket, SeparatedSyntaxList<ArrayElementSyntax> elements,
                                    Token closeBracket) : ExpressionSyntax(TextSpan.FromBounds(openBracket.Span.Start, closeBracket.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token OpenBracket { get; } = openBracket;
    public SeparatedSyntaxList<ArrayElementSyntax> Elements { get; } = elements;
    [ExcludeFromCodeCoverage]
    public Token CloseBracket { get; } = closeBracket;
}

public sealed class ArrayElementSyntax(ExpressionSyntax value, Token? spread) : SyntaxNode(TextSpan.FromBounds(Requires.NotNull(value).Span.Start,
            spread?.Span.End ?? Requires.NotNull(value).Span.End))
{
    public ExpressionSyntax Value { get; } = Requires.NotNull(value);
    public Token? Spread { get; } = spread;
}

public sealed class TypedStructLiteralExpressionSyntax(ExpressionSyntax typeName, Token openBrace,
                                          SeparatedSyntaxList<FieldInitializerSyntax> initializers,
                                          Token closeBrace) : ExpressionSyntax(TextSpan.FromBounds(Requires.NotNull(typeName).Span.Start, closeBrace.Span.End))
{
    public ExpressionSyntax TypeName { get; } = Requires.NotNull(typeName);
    [ExcludeFromCodeCoverage]
    public Token OpenBrace { get; } = openBrace;
    public SeparatedSyntaxList<FieldInitializerSyntax> Initializers { get; } = initializers;
    [ExcludeFromCodeCoverage]
    public Token CloseBrace { get; } = closeBrace;
}

public sealed class RangeExpressionSyntax(ExpressionSyntax start, Token dotDot, ExpressionSyntax end, bool isInclusive) : ExpressionSyntax(TextSpan.FromBounds(Requires.NotNull(start).Span.Start, Requires.NotNull(end).Span.End))
{
    public ExpressionSyntax Start { get; } = Requires.NotNull(start);
    [ExcludeFromCodeCoverage]
    public Token DotDot { get; } = dotDot;
    public ExpressionSyntax End { get; } = Requires.NotNull(end);
    public bool IsInclusive { get; } = isInclusive;
}
