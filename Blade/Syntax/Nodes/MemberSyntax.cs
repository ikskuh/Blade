using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Blade;
using Blade.Source;

namespace Blade.Syntax.Nodes;

/// <summary>
/// Base class for top-level declarations.
/// </summary>
public abstract class MemberSyntax(TextSpan span) : SyntaxNode(span)
{
}

/// <summary>
/// Common interface for function declaration syntax nodes that carry a signature
/// (name, parameters, return spec). Implemented by both <see cref="FunctionDeclarationSyntax"/>
/// and <see cref="AsmFunctionDeclarationSyntax"/>.
/// </summary>
public interface IFunctionSignatureSyntax
{
    Token Name { get; }
    SeparatedSyntaxList<ParameterSyntax> Parameters { get; }
    Token? Arrow { get; }
    SeparatedSyntaxList<ReturnItemSyntax>? ReturnSpec { get; }
}

public sealed class ImportDeclarationSyntax(Token importKeyword, Token source, Token? asKeyword, Token? alias, Token semicolon) : MemberSyntax(TextSpan.FromBounds(importKeyword.Span.Start, semicolon.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token ImportKeyword { get; } = importKeyword;

    /// <summary>
    /// The import source: a string literal for file imports (<c>import "path" as alias;</c>)
    /// or an identifier for named module imports (<c>import extmod;</c>).
    /// </summary>
    public Token Source { get; } = source;

    [ExcludeFromCodeCoverage]
    public Token? AsKeyword { get; } = asKeyword;
    public Token? Alias { get; } = alias;
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; } = semicolon;

    /// <summary>True when the import source is a file path (string literal), false for named modules.</summary>
    public bool IsFileImport => Source.Kind == TokenKind.StringLiteral;
}

public sealed class FunctionDeclarationSyntax(IReadOnlyList<Token> modifiers, Token fnKeyword, Token name, Token openParen,
                                  SeparatedSyntaxList<ParameterSyntax> parameters, Token closeParen,
                                  Token? arrow, SeparatedSyntaxList<ReturnItemSyntax>? returnSpec,
                                  BlockStatementSyntax body) : MemberSyntax(TextSpan.FromBounds(GetStart(modifiers, fnKeyword), Requires.NotNull(body).Span.End)), IFunctionSignatureSyntax
{
    public IReadOnlyList<Token> Modifiers { get; } = Requires.NotNull(modifiers);
    [ExcludeFromCodeCoverage]
    public Token FnKeyword { get; } = fnKeyword;
    public Token Name { get; } = name;
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; } = openParen;
    public SeparatedSyntaxList<ParameterSyntax> Parameters { get; } = parameters;
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; } = closeParen;
    public Token? Arrow { get; } = arrow;
    public SeparatedSyntaxList<ReturnItemSyntax>? ReturnSpec { get; } = returnSpec;
    public BlockStatementSyntax Body { get; } = Requires.NotNull(body);

    private static int GetStart(IReadOnlyList<Token> modifiers, Token fnKeyword)
    {
        IReadOnlyList<Token> checkedModifiers = Requires.NotNull(modifiers);
        return checkedModifiers.Count > 0 ? checkedModifiers[0].Span.Start : fnKeyword.Span.Start;
    }
}

public sealed class VariableDeclarationSyntax(Token? externKeyword, Token? storageClassKeyword, Token mutabilityKeyword,
                                  Token name, Token colon, TypeSyntax type, Token? equalsToken,
                                  ExpressionSyntax? initializer, AddressClauseSyntax? atClause,
                                  AlignClauseSyntax? alignClause, Token semicolon) : MemberSyntax(TextSpan.FromBounds((externKeyword ?? storageClassKeyword ?? mutabilityKeyword).Span.Start, semicolon.Span.End))
{
    public Token? ExternKeyword { get; } = externKeyword;
    public Token? StorageClassKeyword { get; } = storageClassKeyword;
    public Token MutabilityKeyword { get; } = mutabilityKeyword;
    public Token Name { get; } = name;
    [ExcludeFromCodeCoverage]
    public Token Colon { get; } = colon;
    public TypeSyntax Type { get; } = Requires.NotNull(type);
    public Token? EqualsToken { get; } = equalsToken;
    public ExpressionSyntax? Initializer { get; } = initializer;
    public AddressClauseSyntax? AtClause { get; } = atClause;
    public AlignClauseSyntax? AlignClause { get; } = alignClause;
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; } = semicolon;
}

public sealed class TypeAliasDeclarationSyntax(Token typeOrConstKeyword, Token name, Token equalsToken, TypeSyntax type, Token semicolon) : MemberSyntax(TextSpan.FromBounds(typeOrConstKeyword.Span.Start, semicolon.Span.End))
{
    public Token TypeOrConstKeyword { get; } = typeOrConstKeyword;
    public Token Name { get; } = name;
    [ExcludeFromCodeCoverage]
    public Token EqualsToken { get; } = equalsToken;
    public TypeSyntax Type { get; } = Requires.NotNull(type);
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; } = semicolon;
}

public sealed class AsmFunctionDeclarationSyntax(Token asmKeyword, Token? volatileKeyword, Token fnKeyword, Token name,
                                    Token openParen, SeparatedSyntaxList<ParameterSyntax> parameters, Token closeParen,
                                    Token? arrow, SeparatedSyntaxList<ReturnItemSyntax>? returnSpec,
                                    InlineAsmBodySyntax body) : MemberSyntax(TextSpan.FromBounds(asmKeyword.Span.Start, Requires.NotNull(body).Span.End)), IFunctionSignatureSyntax
{
    public Token AsmKeyword { get; } = asmKeyword;
    public Token? VolatileKeyword { get; } = volatileKeyword;
    [ExcludeFromCodeCoverage]
    public Token FnKeyword { get; } = fnKeyword;
    public Token Name { get; } = name;
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; } = openParen;
    public SeparatedSyntaxList<ParameterSyntax> Parameters { get; } = parameters;
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; } = closeParen;
    public Token? Arrow { get; } = arrow;
    public SeparatedSyntaxList<ReturnItemSyntax>? ReturnSpec { get; } = returnSpec;
    public InlineAsmBodySyntax Body { get; } = body;
}

public sealed class GlobalStatementSyntax(StatementSyntax statement) : MemberSyntax(Requires.NotNull(statement).Span)
{
    public StatementSyntax Statement { get; } = Requires.NotNull(statement);
}
