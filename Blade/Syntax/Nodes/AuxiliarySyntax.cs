using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Blade;
using Blade.Source;

namespace Blade.Syntax.Nodes;

public sealed class ParameterSyntax(Token? storageClassKeyword, Token name, Token colon, TypeSyntax type) : SyntaxNode(TextSpan.FromBounds(
            storageClassKeyword?.Span.Start ?? name.Span.Start,
            Requires.NotNull(type).Span.End))
{
    public Token? StorageClassKeyword { get; } = storageClassKeyword;
    public Token Name { get; } = name;
    [ExcludeFromCodeCoverage]
    public Token Colon { get; } = colon;
    public TypeSyntax Type { get; } = Requires.NotNull(type);
}

/// <summary>
/// Represents the mixed declaration-and-statement body of a task declaration.
/// </summary>
public sealed class TaskBodySyntax(Token openBrace, IReadOnlyList<SyntaxNode> items, Token closeBrace) : SyntaxNode(TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End))
{
    /// <summary>
    /// Gets the opening brace token of the task body.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public Token OpenBrace { get; } = openBrace;

    /// <summary>
    /// Gets the declarations and statements contained in the task body.
    /// </summary>
    public IReadOnlyList<SyntaxNode> Items { get; } = Requires.NotNull(items);

    /// <summary>
    /// Gets the closing brace token of the task body.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public Token CloseBrace { get; } = closeBrace;
}

public sealed class ReturnItemSyntax(Token? name, Token? colonToken, TypeSyntax type, FlagAnnotationSyntax? flagAnnotation) : SyntaxNode(TextSpan.FromBounds(
            name?.Span.Start ?? Requires.NotNull(type).Span.Start,
            flagAnnotation?.Span.End ?? Requires.NotNull(type).Span.End))
{
    public Token? Name { get; } = name;
    public Token? ColonToken { get; } = colonToken;
    public TypeSyntax Type { get; } = Requires.NotNull(type);
    public FlagAnnotationSyntax? FlagAnnotation { get; } = flagAnnotation;
}

public sealed class FlagAnnotationSyntax(Token atToken, Token flag) : SyntaxNode(TextSpan.FromBounds(atToken.Span.Start, flag.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token AtToken { get; } = atToken;
    public Token Flag { get; } = flag;
}

public sealed class AddressClauseSyntax(Token atToken, Token openParen, ExpressionSyntax address, Token closeParen) : SyntaxNode(TextSpan.FromBounds(atToken.Span.Start, closeParen.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token AtToken { get; } = atToken;
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; } = openParen;
    public ExpressionSyntax Address { get; } = Requires.NotNull(address);
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; } = closeParen;
}

public sealed class AlignClauseSyntax(Token alignKeyword, Token openParen, ExpressionSyntax alignment, Token closeParen) : SyntaxNode(TextSpan.FromBounds(alignKeyword.Span.Start, closeParen.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token AlignKeyword { get; } = alignKeyword;
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; } = openParen;
    public ExpressionSyntax Alignment { get; } = Requires.NotNull(alignment);
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; } = closeParen;
}

public sealed class ElseClauseSyntax(Token elseKeyword, StatementSyntax body) : SyntaxNode(TextSpan.FromBounds(elseKeyword.Span.Start, Requires.NotNull(body).Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token ElseKeyword { get; } = elseKeyword;
    public StatementSyntax Body { get; } = Requires.NotNull(body);
}

public sealed class FieldInitializerSyntax(Token dot, Token name, Token equalsToken, ExpressionSyntax value) : SyntaxNode(TextSpan.FromBounds(dot.Span.Start, Requires.NotNull(value).Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token Dot { get; } = dot;
    public Token Name { get; } = name;
    public Token EqualsToken { get; } = equalsToken;
    public ExpressionSyntax Value { get; } = Requires.NotNull(value);
}

public sealed class StructFieldSyntax(Token name, Token colon, TypeSyntax type) : SyntaxNode(TextSpan.FromBounds(name.Span.Start, Requires.NotNull(type).Span.End))
{
    public Token Name { get; } = name;
    [ExcludeFromCodeCoverage]
    public Token Colon { get; } = colon;
    public TypeSyntax Type { get; } = Requires.NotNull(type);
}

public sealed class AsmOutputBindingSyntax(Token arrow, Token name, Token colon, TypeSyntax type,
                              FlagAnnotationSyntax? flagAnnotation) : SyntaxNode(TextSpan.FromBounds(arrow.Span.Start,
            flagAnnotation?.Span.End ?? Requires.NotNull(type).Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token Arrow { get; } = arrow;
    public Token Name { get; } = name;
    [ExcludeFromCodeCoverage]
    public Token Colon { get; } = colon;
    public TypeSyntax Type { get; } = Requires.NotNull(type);
    public FlagAnnotationSyntax? FlagAnnotation { get; } = flagAnnotation;
}

public sealed class EnumMemberSyntax(Token name, Token? equalsToken, ExpressionSyntax? value, bool isOpenMarker = false) : SyntaxNode(TextSpan.FromBounds(name.Span.Start, value?.Span.End ?? name.Span.End))
{
    public Token Name { get; } = name;
    public Token? EqualsToken { get; } = equalsToken;
    public ExpressionSyntax? Value { get; } = value;
    public bool IsOpenMarker { get; } = isOpenMarker;
}

public sealed class ForBindingSyntax(Token arrow, Token? ampersand, Token itemName, Token? comma, Token? indexName) : SyntaxNode(TextSpan.FromBounds(arrow.Span.Start, indexName?.Span.End ?? itemName.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token Arrow { get; } = arrow;
    public Token? Ampersand { get; } = ampersand;
    public Token ItemName { get; } = itemName;
    public Token? Comma { get; } = comma;
    public Token? IndexName { get; } = indexName;
}

public sealed class NamedArgumentSyntax(Token name, Token equalsToken, ExpressionSyntax value) : ExpressionSyntax(TextSpan.FromBounds(name.Span.Start, Requires.NotNull(value).Span.End))
{
    public Token Name { get; } = name;
    [ExcludeFromCodeCoverage]
    public Token EqualsToken { get; } = equalsToken;
    public ExpressionSyntax Value { get; } = Requires.NotNull(value);
}
