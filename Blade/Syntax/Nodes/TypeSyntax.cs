using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Blade;
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
    [ExcludeFromCodeCoverage]
    public Token Keyword { get; }

    public PrimitiveTypeSyntax(Token keyword)
        : base(keyword.Span)
    {
        Keyword = keyword;
    }
}

public sealed class GenericWidthTypeSyntax : TypeSyntax
{
    [ExcludeFromCodeCoverage]
    public Token Keyword { get; }

    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; }

    public ExpressionSyntax Width { get; }

    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; }

    public GenericWidthTypeSyntax(Token keyword, Token openParen, ExpressionSyntax width, Token closeParen)
        : base(TextSpan.FromBounds(keyword.Span.Start, closeParen.Span.End))
    {
        Keyword = keyword;
        OpenParen = openParen;
        Width = Requires.NotNull(width);
        CloseParen = closeParen;
    }
}

public sealed class ArrayTypeSyntax : TypeSyntax
{
    [ExcludeFromCodeCoverage]
    public Token OpenBracket { get; }
    public ExpressionSyntax Size { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseBracket { get; }
    public TypeSyntax ElementType { get; }

    public ArrayTypeSyntax(Token openBracket, ExpressionSyntax size, Token closeBracket, TypeSyntax elementType)
        : base(TextSpan.FromBounds(openBracket.Span.Start, Requires.NotNull(elementType).Span.End))
    {
        OpenBracket = openBracket;
        Size = Requires.NotNull(size);
        CloseBracket = closeBracket;
        ElementType = Requires.NotNull(elementType);
    }
}

public sealed class PointerTypeSyntax : TypeSyntax
{
    [ExcludeFromCodeCoverage]
    public Token Star { get; }
    public Token? StorageClassKeyword { get; }
    [ExcludeFromCodeCoverage]
    public Token? ConstKeyword { get; }
    public Token? VolatileKeyword { get; }
    public AlignClauseSyntax? AlignClause { get; }
    public TypeSyntax PointeeType { get; }

    public PointerTypeSyntax(Token star, Token? storageClassKeyword, Token? constKeyword,
                             Token? volatileKeyword, AlignClauseSyntax? alignClause, TypeSyntax pointeeType)
        : base(TextSpan.FromBounds(star.Span.Start, Requires.NotNull(pointeeType).Span.End))
    {
        Star = star;
        StorageClassKeyword = storageClassKeyword;
        ConstKeyword = constKeyword;
        VolatileKeyword = volatileKeyword;
        AlignClause = alignClause;
        PointeeType = Requires.NotNull(pointeeType);
    }
}

public sealed class MultiPointerTypeSyntax : TypeSyntax
{
    [ExcludeFromCodeCoverage]
    public Token OpenBracket { get; }
    [ExcludeFromCodeCoverage]
    public Token Star { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseBracket { get; }
    public Token? StorageClassKeyword { get; }
    public Token? ConstKeyword { get; }
    public Token? VolatileKeyword { get; }
    public AlignClauseSyntax? AlignClause { get; }
    public TypeSyntax PointeeType { get; }

    public MultiPointerTypeSyntax(Token openBracket, Token star, Token closeBracket,
                                  Token? storageClassKeyword, Token? constKeyword,
                                  Token? volatileKeyword, AlignClauseSyntax? alignClause,
                                  TypeSyntax pointeeType)
        : base(TextSpan.FromBounds(openBracket.Span.Start, Requires.NotNull(pointeeType).Span.End))
    {
        OpenBracket = openBracket;
        Star = star;
        CloseBracket = closeBracket;
        StorageClassKeyword = storageClassKeyword;
        ConstKeyword = constKeyword;
        VolatileKeyword = volatileKeyword;
        AlignClause = alignClause;
        PointeeType = Requires.NotNull(pointeeType);
    }
}

public sealed class StructTypeSyntax : TypeSyntax
{
    [ExcludeFromCodeCoverage]
    public Token StructKeyword { get; }
    [ExcludeFromCodeCoverage]
    public Token OpenBrace { get; }
    public SeparatedSyntaxList<StructFieldSyntax> Fields { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseBrace { get; }

    public StructTypeSyntax(Token structKeyword, Token openBrace,
                            SeparatedSyntaxList<StructFieldSyntax> fields, Token closeBrace)
        : base(TextSpan.FromBounds(structKeyword.Span.Start, closeBrace.Span.End))
    {
        StructKeyword = structKeyword;
        OpenBrace = openBrace;
        Fields = fields;
        CloseBrace = closeBrace;
    }
}

public sealed class UnionTypeSyntax : TypeSyntax
{
    [ExcludeFromCodeCoverage]
    public Token UnionKeyword { get; }
    [ExcludeFromCodeCoverage]
    public Token OpenBrace { get; }
    public SeparatedSyntaxList<StructFieldSyntax> Fields { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseBrace { get; }

    public UnionTypeSyntax(Token unionKeyword, Token openBrace,
                           SeparatedSyntaxList<StructFieldSyntax> fields, Token closeBrace)
        : base(TextSpan.FromBounds(unionKeyword.Span.Start, closeBrace.Span.End))
    {
        UnionKeyword = unionKeyword;
        OpenBrace = openBrace;
        Fields = fields;
        CloseBrace = closeBrace;
    }
}

public sealed class EnumTypeSyntax : TypeSyntax
{
    [ExcludeFromCodeCoverage]
    public Token EnumKeyword { get; }
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; }
    public TypeSyntax BackingType { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; }
    [ExcludeFromCodeCoverage]
    public Token OpenBrace { get; }
    public SeparatedSyntaxList<EnumMemberSyntax> Members { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseBrace { get; }

    public EnumTypeSyntax(Token enumKeyword, Token openParen, TypeSyntax backingType, Token closeParen,
                          Token openBrace, SeparatedSyntaxList<EnumMemberSyntax> members, Token closeBrace)
        : base(TextSpan.FromBounds(enumKeyword.Span.Start, closeBrace.Span.End))
    {
        EnumKeyword = enumKeyword;
        OpenParen = openParen;
        BackingType = Requires.NotNull(backingType);
        CloseParen = closeParen;
        OpenBrace = openBrace;
        Members = members;
        CloseBrace = closeBrace;
    }
}

public sealed class BitfieldTypeSyntax : TypeSyntax
{
    [ExcludeFromCodeCoverage]

    public Token BitfieldKeyword { get; }
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; }
    public TypeSyntax BackingType { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; }
    [ExcludeFromCodeCoverage]
    public Token OpenBrace { get; }
    public SeparatedSyntaxList<StructFieldSyntax> Fields { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseBrace { get; }

    public BitfieldTypeSyntax(Token bitfieldKeyword, Token openParen, TypeSyntax backingType, Token closeParen,
                              Token openBrace, SeparatedSyntaxList<StructFieldSyntax> fields, Token closeBrace)
        : base(TextSpan.FromBounds(bitfieldKeyword.Span.Start, closeBrace.Span.End))
    {
        BitfieldKeyword = bitfieldKeyword;
        OpenParen = openParen;
        BackingType = Requires.NotNull(backingType);
        CloseParen = closeParen;
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

public sealed class QualifiedTypeSyntax : TypeSyntax
{
    public QualifiedTypeSyntax(IReadOnlyList<Token> parts)
        : base(TextSpan.FromBounds(Requires.NotNull(parts)[0].Span.Start, parts[^1].Span.End))
    {
        Parts = parts;
    }

    public IReadOnlyList<Token> Parts { get; }
}
