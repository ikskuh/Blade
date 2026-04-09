using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Blade;
using Blade.Source;

namespace Blade.Syntax.Nodes;

/// <summary>
/// Base class for all type syntax nodes.
/// </summary>
public abstract class TypeSyntax(TextSpan span) : SyntaxNode(span)
{
}

public sealed class PrimitiveTypeSyntax(Token keyword) : TypeSyntax(keyword.Span)
{
    [ExcludeFromCodeCoverage]
    public Token Keyword { get; } = keyword;
}

public sealed class GenericWidthTypeSyntax(Token keyword, Token openParen, ExpressionSyntax width, Token closeParen) : TypeSyntax(TextSpan.FromBounds(keyword.Span.Start, closeParen.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token Keyword { get; } = keyword;

    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; } = openParen;

    public ExpressionSyntax Width { get; } = Requires.NotNull(width);

    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; } = closeParen;
}

public sealed class ArrayTypeSyntax(Token openBracket, ExpressionSyntax size, Token closeBracket, TypeSyntax elementType) : TypeSyntax(TextSpan.FromBounds(openBracket.Span.Start, Requires.NotNull(elementType).Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token OpenBracket { get; } = openBracket;
    public ExpressionSyntax Size { get; } = Requires.NotNull(size);
    [ExcludeFromCodeCoverage]
    public Token CloseBracket { get; } = closeBracket;
    public TypeSyntax ElementType { get; } = Requires.NotNull(elementType);
}

public sealed class PointerTypeSyntax(Token star, Token? storageClassKeyword, Token? constKeyword,
                         Token? volatileKeyword, AlignClauseSyntax? alignClause, TypeSyntax pointeeType) : TypeSyntax(TextSpan.FromBounds(star.Span.Start, Requires.NotNull(pointeeType).Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token Star { get; } = star;
    public Token? StorageClassKeyword { get; } = storageClassKeyword;
    [ExcludeFromCodeCoverage]
    public Token? ConstKeyword { get; } = constKeyword;
    public Token? VolatileKeyword { get; } = volatileKeyword;
    public AlignClauseSyntax? AlignClause { get; } = alignClause;
    public TypeSyntax PointeeType { get; } = Requires.NotNull(pointeeType);
}

public sealed class MultiPointerTypeSyntax(Token openBracket, Token star, Token closeBracket,
                              Token? storageClassKeyword, Token? constKeyword,
                              Token? volatileKeyword, AlignClauseSyntax? alignClause,
                              TypeSyntax pointeeType) : TypeSyntax(TextSpan.FromBounds(openBracket.Span.Start, Requires.NotNull(pointeeType).Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token OpenBracket { get; } = openBracket;
    [ExcludeFromCodeCoverage]
    public Token Star { get; } = star;
    [ExcludeFromCodeCoverage]
    public Token CloseBracket { get; } = closeBracket;
    public Token? StorageClassKeyword { get; } = storageClassKeyword;
    public Token? ConstKeyword { get; } = constKeyword;
    public Token? VolatileKeyword { get; } = volatileKeyword;
    public AlignClauseSyntax? AlignClause { get; } = alignClause;
    public TypeSyntax PointeeType { get; } = Requires.NotNull(pointeeType);
}

public sealed class StructTypeSyntax(Token structKeyword, Token openBrace,
                        SeparatedSyntaxList<StructFieldSyntax> fields, Token closeBrace) : TypeSyntax(TextSpan.FromBounds(structKeyword.Span.Start, closeBrace.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token StructKeyword { get; } = structKeyword;
    [ExcludeFromCodeCoverage]
    public Token OpenBrace { get; } = openBrace;
    public SeparatedSyntaxList<StructFieldSyntax> Fields { get; } = fields;
    [ExcludeFromCodeCoverage]
    public Token CloseBrace { get; } = closeBrace;
}

public sealed class UnionTypeSyntax(Token unionKeyword, Token openBrace,
                       SeparatedSyntaxList<StructFieldSyntax> fields, Token closeBrace) : TypeSyntax(TextSpan.FromBounds(unionKeyword.Span.Start, closeBrace.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token UnionKeyword { get; } = unionKeyword;
    [ExcludeFromCodeCoverage]
    public Token OpenBrace { get; } = openBrace;
    public SeparatedSyntaxList<StructFieldSyntax> Fields { get; } = fields;
    [ExcludeFromCodeCoverage]
    public Token CloseBrace { get; } = closeBrace;
}

public sealed class EnumTypeSyntax(Token enumKeyword, Token openParen, TypeSyntax backingType, Token closeParen,
                      Token openBrace, SeparatedSyntaxList<EnumMemberSyntax> members, Token closeBrace) : TypeSyntax(TextSpan.FromBounds(enumKeyword.Span.Start, closeBrace.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token EnumKeyword { get; } = enumKeyword;
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; } = openParen;
    public TypeSyntax BackingType { get; } = Requires.NotNull(backingType);
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; } = closeParen;
    [ExcludeFromCodeCoverage]
    public Token OpenBrace { get; } = openBrace;
    public SeparatedSyntaxList<EnumMemberSyntax> Members { get; } = members;
    [ExcludeFromCodeCoverage]
    public Token CloseBrace { get; } = closeBrace;
}

public sealed class BitfieldTypeSyntax(Token bitfieldKeyword, Token openParen, TypeSyntax backingType, Token closeParen,
                          Token openBrace, SeparatedSyntaxList<StructFieldSyntax> fields, Token closeBrace) : TypeSyntax(TextSpan.FromBounds(bitfieldKeyword.Span.Start, closeBrace.Span.End))
{
    [ExcludeFromCodeCoverage]

    public Token BitfieldKeyword { get; } = bitfieldKeyword;
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; } = openParen;
    public TypeSyntax BackingType { get; } = Requires.NotNull(backingType);
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; } = closeParen;
    [ExcludeFromCodeCoverage]
    public Token OpenBrace { get; } = openBrace;
    public SeparatedSyntaxList<StructFieldSyntax> Fields { get; } = fields;
    [ExcludeFromCodeCoverage]
    public Token CloseBrace { get; } = closeBrace;
}

public sealed class NamedTypeSyntax(Token name) : TypeSyntax(name.Span)
{
    public Token Name { get; } = name;
}

public sealed class QualifiedTypeSyntax(IReadOnlyList<Token> parts) : TypeSyntax(TextSpan.FromBounds(Requires.NotNull(parts)[0].Span.Start, parts[^1].Span.End))
{
    public IReadOnlyList<Token> Parts { get; } = parts;
}
