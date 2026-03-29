using System.Diagnostics.CodeAnalysis;
using Blade;
using Blade.Source;

namespace Blade.Syntax.Nodes;

public sealed class ParameterSyntax : SyntaxNode
{
    public Token? StorageClassKeyword { get; }
    public Token Name { get; }
    [ExcludeFromCodeCoverage]
    public Token Colon { get; }
    public TypeSyntax Type { get; }

    public ParameterSyntax(Token? storageClassKeyword, Token name, Token colon, TypeSyntax type)
        : base(TextSpan.FromBounds(
            storageClassKeyword?.Span.Start ?? name.Span.Start,
            Requires.NotNull(type).Span.End))
    {
        StorageClassKeyword = storageClassKeyword;
        Name = name;
        Colon = colon;
        Type = Requires.NotNull(type);
    }
}

public sealed class ReturnItemSyntax : SyntaxNode
{
    public Token? Name { get; }
    public Token? ColonToken { get; }
    public TypeSyntax Type { get; }
    public FlagAnnotationSyntax? FlagAnnotation { get; }

    public ReturnItemSyntax(Token? name, Token? colonToken, TypeSyntax type, FlagAnnotationSyntax? flagAnnotation)
        : base(TextSpan.FromBounds(
            name?.Span.Start ?? Requires.NotNull(type).Span.Start,
            flagAnnotation?.Span.End ?? Requires.NotNull(type).Span.End))
    {
        Name = name;
        ColonToken = colonToken;
        Type = Requires.NotNull(type);
        FlagAnnotation = flagAnnotation;
    }
}

public sealed class FlagAnnotationSyntax : SyntaxNode
{
    [ExcludeFromCodeCoverage]
    public Token AtToken { get; }
    public Token Flag { get; }

    public FlagAnnotationSyntax(Token atToken, Token flag)
        : base(TextSpan.FromBounds(atToken.Span.Start, flag.Span.End))
    {
        AtToken = atToken;
        Flag = flag;
    }
}

public sealed class AddressClauseSyntax : SyntaxNode
{
    [ExcludeFromCodeCoverage]
    public Token AtToken { get; }
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; }
    public ExpressionSyntax Address { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; }

    public AddressClauseSyntax(Token atToken, Token openParen, ExpressionSyntax address, Token closeParen)
        : base(TextSpan.FromBounds(atToken.Span.Start, closeParen.Span.End))
    {
        AtToken = atToken;
        OpenParen = openParen;
        Address = Requires.NotNull(address);
        CloseParen = closeParen;
    }
}

public sealed class AlignClauseSyntax : SyntaxNode
{
    [ExcludeFromCodeCoverage]
    public Token AlignKeyword { get; }
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; }
    public ExpressionSyntax Alignment { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; }

    public AlignClauseSyntax(Token alignKeyword, Token openParen, ExpressionSyntax alignment, Token closeParen)
        : base(TextSpan.FromBounds(alignKeyword.Span.Start, closeParen.Span.End))
    {
        AlignKeyword = alignKeyword;
        OpenParen = openParen;
        Alignment = Requires.NotNull(alignment);
        CloseParen = closeParen;
    }
}

public sealed class ElseClauseSyntax : SyntaxNode
{
    [ExcludeFromCodeCoverage]
    public Token ElseKeyword { get; }
    public StatementSyntax Body { get; }

    public ElseClauseSyntax(Token elseKeyword, StatementSyntax body)
        : base(TextSpan.FromBounds(elseKeyword.Span.Start, Requires.NotNull(body).Span.End))
    {
        ElseKeyword = elseKeyword;
        Body = Requires.NotNull(body);
    }
}

public sealed class FieldInitializerSyntax : SyntaxNode
{
    [ExcludeFromCodeCoverage]
    public Token Dot { get; }
    public Token Name { get; }
    public Token EqualsToken { get; }
    public ExpressionSyntax Value { get; }

    public FieldInitializerSyntax(Token dot, Token name, Token equalsToken, ExpressionSyntax value)
        : base(TextSpan.FromBounds(dot.Span.Start, Requires.NotNull(value).Span.End))
    {
        Dot = dot;
        Name = name;
        EqualsToken = equalsToken;
        Value = Requires.NotNull(value);
    }
}

public sealed class StructFieldSyntax : SyntaxNode
{
    public Token Name { get; }
    [ExcludeFromCodeCoverage]
    public Token Colon { get; }
    public TypeSyntax Type { get; }

    public StructFieldSyntax(Token name, Token colon, TypeSyntax type)
        : base(TextSpan.FromBounds(name.Span.Start, Requires.NotNull(type).Span.End))
    {
        Name = name;
        Colon = colon;
        Type = Requires.NotNull(type);
    }
}

public sealed class AsmOutputBindingSyntax : SyntaxNode
{
    [ExcludeFromCodeCoverage]
    public Token Arrow { get; }
    public Token Name { get; }
    [ExcludeFromCodeCoverage]
    public Token Colon { get; }
    public TypeSyntax Type { get; }
    public FlagAnnotationSyntax? FlagAnnotation { get; }

    public AsmOutputBindingSyntax(Token arrow, Token name, Token colon, TypeSyntax type,
                                  FlagAnnotationSyntax? flagAnnotation)
        : base(TextSpan.FromBounds(arrow.Span.Start,
            flagAnnotation?.Span.End ?? Requires.NotNull(type).Span.End))
    {
        Arrow = arrow;
        Name = name;
        Colon = colon;
        Type = Requires.NotNull(type);
        FlagAnnotation = flagAnnotation;
    }
}

public sealed class EnumMemberSyntax : SyntaxNode
{
    public Token Name { get; }
    public Token? EqualsToken { get; }
    public ExpressionSyntax? Value { get; }
    public bool IsOpenMarker { get; }

    public EnumMemberSyntax(Token name, Token? equalsToken, ExpressionSyntax? value, bool isOpenMarker = false)
        : base(TextSpan.FromBounds(name.Span.Start, value?.Span.End ?? name.Span.End))
    {
        Name = name;
        EqualsToken = equalsToken;
        Value = value;
        IsOpenMarker = isOpenMarker;
    }
}

public sealed class ForBindingSyntax : SyntaxNode
{
    [ExcludeFromCodeCoverage]
    public Token Arrow { get; }
    public Token? Ampersand { get; }
    public Token ItemName { get; }
    public Token? Comma { get; }
    public Token? IndexName { get; }

    public ForBindingSyntax(Token arrow, Token? ampersand, Token itemName, Token? comma, Token? indexName)
        : base(TextSpan.FromBounds(arrow.Span.Start, indexName?.Span.End ?? itemName.Span.End))
    {
        Arrow = arrow;
        Ampersand = ampersand;
        ItemName = itemName;
        Comma = comma;
        IndexName = indexName;
    }
}

public sealed class NamedArgumentSyntax : ExpressionSyntax
{
    public Token Name { get; }
    [ExcludeFromCodeCoverage]
    public Token EqualsToken { get; }
    public ExpressionSyntax Value { get; }

    public NamedArgumentSyntax(Token name, Token equalsToken, ExpressionSyntax value)
        : base(TextSpan.FromBounds(name.Span.Start, Requires.NotNull(value).Span.End))
    {
        Name = name;
        EqualsToken = equalsToken;
        Value = Requires.NotNull(value);
    }
}
