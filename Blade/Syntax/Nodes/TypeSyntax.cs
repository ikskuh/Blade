using Blade.Source;

namespace Blade.Syntax.Nodes;

/// <summary>
/// Base class for all type syntax nodes.
/// </summary>
public abstract class TypeSyntax : SyntaxNode
{
    protected TypeSyntax(TextSpan span) : base(span) { }
}

public sealed class PrimitiveTypeSyntax : TypeSyntax
{
    public Token Keyword { get; }

    public PrimitiveTypeSyntax(Token keyword)
        : base(keyword.Span)
    {
        Keyword = keyword;
    }
}

public sealed class GenericWidthTypeSyntax : TypeSyntax
{
    public Token Keyword { get; }
    public Token OpenParen { get; }
    public ExpressionSyntax Width { get; }
    public Token CloseParen { get; }

    public GenericWidthTypeSyntax(Token keyword, Token openParen, ExpressionSyntax width, Token closeParen)
        : base(TextSpan.FromBounds(keyword.Span.Start, closeParen.Span.End))
    {
        Keyword = keyword;
        OpenParen = openParen;
        Width = width;
        CloseParen = closeParen;
    }
}

public sealed class ArrayTypeSyntax : TypeSyntax
{
    public Token OpenBracket { get; }
    public ExpressionSyntax Size { get; }
    public Token CloseBracket { get; }
    public TypeSyntax ElementType { get; }

    public ArrayTypeSyntax(Token openBracket, ExpressionSyntax size, Token closeBracket, TypeSyntax elementType)
        : base(TextSpan.FromBounds(openBracket.Span.Start, elementType.Span.End))
    {
        OpenBracket = openBracket;
        Size = size;
        CloseBracket = closeBracket;
        ElementType = elementType;
    }
}

public sealed class PointerTypeSyntax : TypeSyntax
{
    public Token Star { get; }
    public Token? ConstKeyword { get; }
    public TypeSyntax PointeeType { get; }

    public PointerTypeSyntax(Token star, Token? constKeyword, TypeSyntax pointeeType)
        : base(TextSpan.FromBounds(star.Span.Start, pointeeType.Span.End))
    {
        Star = star;
        ConstKeyword = constKeyword;
        PointeeType = pointeeType;
    }
}

public sealed class StructTypeSyntax : TypeSyntax
{
    public Token PackedKeyword { get; }
    public Token StructKeyword { get; }
    public Token OpenBrace { get; }
    public SeparatedSyntaxList<StructFieldSyntax> Fields { get; }
    public Token CloseBrace { get; }

    public StructTypeSyntax(Token packedKeyword, Token structKeyword, Token openBrace, SeparatedSyntaxList<StructFieldSyntax> fields, Token closeBrace)
        : base(TextSpan.FromBounds(packedKeyword.Span.Start, closeBrace.Span.End))
    {
        PackedKeyword = packedKeyword;
        StructKeyword = structKeyword;
        OpenBrace = openBrace;
        Fields = fields;
        CloseBrace = closeBrace;
    }
}

public sealed class NamedTypeSyntax : TypeSyntax
{
    public Token Name { get; }

    public NamedTypeSyntax(Token name)
        : base(name.Span)
    {
        Name = name;
    }
}
