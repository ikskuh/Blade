using System.Collections.Generic;
using Blade;
using Blade.Source;

namespace Blade.Syntax.Nodes;

/// <summary>
/// Base class for top-level declarations.
/// </summary>
public abstract class MemberSyntax : SyntaxNode
{
    protected MemberSyntax(TextSpan span) : base(span) { }
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

public sealed class ImportDeclarationSyntax : MemberSyntax
{
    public Token ImportKeyword { get; }

    /// <summary>
    /// The import source: a string literal for file imports (<c>import "path" as alias;</c>)
    /// or an identifier for named module imports (<c>import extmod;</c>).
    /// </summary>
    public Token Source { get; }

    public Token? AsKeyword { get; }
    public Token? Alias { get; }
    public Token Semicolon { get; }

    public ImportDeclarationSyntax(Token importKeyword, Token source, Token? asKeyword, Token? alias, Token semicolon)
        : base(TextSpan.FromBounds(importKeyword.Span.Start, semicolon.Span.End))
    {
        ImportKeyword = importKeyword;
        Source = source;
        AsKeyword = asKeyword;
        Alias = alias;
        Semicolon = semicolon;
    }

    /// <summary>True when the import source is a file path (string literal), false for named modules.</summary>
    public bool IsFileImport => Source.Kind == TokenKind.StringLiteral;
}

public sealed class FunctionDeclarationSyntax : MemberSyntax, IFunctionSignatureSyntax
{
    public Token? FuncKindKeyword { get; }
    public Token FnKeyword { get; }
    public Token Name { get; }
    public Token OpenParen { get; }
    public SeparatedSyntaxList<ParameterSyntax> Parameters { get; }
    public Token CloseParen { get; }
    public Token? Arrow { get; }
    public SeparatedSyntaxList<ReturnItemSyntax>? ReturnSpec { get; }
    public BlockStatementSyntax Body { get; }

    public FunctionDeclarationSyntax(Token? funcKindKeyword, Token fnKeyword, Token name, Token openParen,
                                      SeparatedSyntaxList<ParameterSyntax> parameters, Token closeParen,
                                      Token? arrow, SeparatedSyntaxList<ReturnItemSyntax>? returnSpec,
                                      BlockStatementSyntax body)
        : base(TextSpan.FromBounds(funcKindKeyword?.Span.Start ?? fnKeyword.Span.Start, Requires.NotNull(body).Span.End))
    {
        FuncKindKeyword = funcKindKeyword;
        FnKeyword = fnKeyword;
        Name = name;
        OpenParen = openParen;
        Parameters = parameters;
        CloseParen = closeParen;
        Arrow = arrow;
        ReturnSpec = returnSpec;
        Body = Requires.NotNull(body);
    }
}

public sealed class VariableDeclarationSyntax : MemberSyntax
{
    public Token? ExternKeyword { get; }
    public Token? StorageClassKeyword { get; }
    public Token MutabilityKeyword { get; }
    public Token Name { get; }
    public Token Colon { get; }
    public TypeSyntax Type { get; }
    public Token? EqualsToken { get; }
    public ExpressionSyntax? Initializer { get; }
    public AddressClauseSyntax? AtClause { get; }
    public AlignClauseSyntax? AlignClause { get; }
    public Token Semicolon { get; }

    public VariableDeclarationSyntax(Token? externKeyword, Token? storageClassKeyword, Token mutabilityKeyword,
                                      Token name, Token colon, TypeSyntax type, Token? equalsToken,
                                      ExpressionSyntax? initializer, AddressClauseSyntax? atClause,
                                      AlignClauseSyntax? alignClause, Token semicolon)
        : base(TextSpan.FromBounds((externKeyword ?? storageClassKeyword ?? mutabilityKeyword).Span.Start, semicolon.Span.End))
    {
        ExternKeyword = externKeyword;
        StorageClassKeyword = storageClassKeyword;
        MutabilityKeyword = mutabilityKeyword;
        Name = name;
        Colon = colon;
        Type = Requires.NotNull(type);
        EqualsToken = equalsToken;
        Initializer = initializer;
        AtClause = atClause;
        AlignClause = alignClause;
        Semicolon = semicolon;
    }
}

public sealed class TypeAliasDeclarationSyntax : MemberSyntax
{
    public Token TypeOrConstKeyword { get; }
    public Token Name { get; }
    public Token EqualsToken { get; }
    public TypeSyntax Type { get; }
    public Token Semicolon { get; }

    public TypeAliasDeclarationSyntax(Token typeOrConstKeyword, Token name, Token equalsToken, TypeSyntax type, Token semicolon)
        : base(TextSpan.FromBounds(typeOrConstKeyword.Span.Start, semicolon.Span.End))
    {
        TypeOrConstKeyword = typeOrConstKeyword;
        Name = name;
        EqualsToken = equalsToken;
        Type = Requires.NotNull(type);
        Semicolon = semicolon;
    }
}

public sealed class AsmFunctionDeclarationSyntax : MemberSyntax, IFunctionSignatureSyntax
{
    public Token AsmKeyword { get; }
    public Token? VolatileKeyword { get; }
    public Token FnKeyword { get; }
    public Token Name { get; }
    public Token OpenParen { get; }
    public SeparatedSyntaxList<ParameterSyntax> Parameters { get; }
    public Token CloseParen { get; }
    public Token? Arrow { get; }
    public SeparatedSyntaxList<ReturnItemSyntax>? ReturnSpec { get; }
    public Token OpenBrace { get; }
    public string Body { get; }
    public Token CloseBrace { get; }

    public AsmFunctionDeclarationSyntax(Token asmKeyword, Token? volatileKeyword, Token fnKeyword, Token name,
                                        Token openParen, SeparatedSyntaxList<ParameterSyntax> parameters, Token closeParen,
                                        Token? arrow, SeparatedSyntaxList<ReturnItemSyntax>? returnSpec,
                                        Token openBrace, string body, Token closeBrace)
        : base(TextSpan.FromBounds(asmKeyword.Span.Start, closeBrace.Span.End))
    {
        AsmKeyword = asmKeyword;
        VolatileKeyword = volatileKeyword;
        FnKeyword = fnKeyword;
        Name = name;
        OpenParen = openParen;
        Parameters = parameters;
        CloseParen = closeParen;
        Arrow = arrow;
        ReturnSpec = returnSpec;
        OpenBrace = openBrace;
        Body = body;
        CloseBrace = closeBrace;
    }
}

public sealed class GlobalStatementSyntax : MemberSyntax
{
    public StatementSyntax Statement { get; }

    public GlobalStatementSyntax(StatementSyntax statement)
        : base(Requires.NotNull(statement).Span)
    {
        Statement = Requires.NotNull(statement);
    }
}
