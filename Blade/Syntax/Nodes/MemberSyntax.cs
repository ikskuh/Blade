using System.Collections.Generic;
using Blade.Source;

namespace Blade.Syntax.Nodes;

/// <summary>
/// Base class for top-level declarations.
/// </summary>
public abstract class MemberSyntax : SyntaxNode
{
    protected MemberSyntax(TextSpan span) : base(span) { }
}

public sealed class ImportDeclarationSyntax : MemberSyntax
{
    public Token ImportKeyword { get; }
    public Token Path { get; }
    public Token AsKeyword { get; }
    public Token Alias { get; }
    public Token Semicolon { get; }

    public ImportDeclarationSyntax(Token importKeyword, Token path, Token asKeyword, Token alias, Token semicolon)
        : base(TextSpan.FromBounds(importKeyword.Span.Start, semicolon.Span.End))
    {
        ImportKeyword = importKeyword;
        Path = path;
        AsKeyword = asKeyword;
        Alias = alias;
        Semicolon = semicolon;
    }
}

public sealed class FunctionDeclarationSyntax : MemberSyntax
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
        : base(TextSpan.FromBounds(funcKindKeyword?.Span.Start ?? fnKeyword.Span.Start, body.Span.End))
    {
        FuncKindKeyword = funcKindKeyword;
        FnKeyword = fnKeyword;
        Name = name;
        OpenParen = openParen;
        Parameters = parameters;
        CloseParen = closeParen;
        Arrow = arrow;
        ReturnSpec = returnSpec;
        Body = body;
    }
}

public sealed class VariableDeclarationSyntax : MemberSyntax
{
    public Token? ExternKeyword { get; }
    public Token StorageClassKeyword { get; }
    public Token MutabilityKeyword { get; }
    public Token Name { get; }
    public Token Colon { get; }
    public TypeSyntax Type { get; }
    public Token? EqualsToken { get; }
    public ExpressionSyntax? Initializer { get; }
    public AddressClauseSyntax? AtClause { get; }
    public AlignClauseSyntax? AlignClause { get; }
    public Token Semicolon { get; }

    public VariableDeclarationSyntax(Token? externKeyword, Token storageClassKeyword, Token mutabilityKeyword,
                                      Token name, Token colon, TypeSyntax type, Token? equalsToken,
                                      ExpressionSyntax? initializer, AddressClauseSyntax? atClause,
                                      AlignClauseSyntax? alignClause, Token semicolon)
        : base(TextSpan.FromBounds(externKeyword?.Span.Start ?? storageClassKeyword.Span.Start, semicolon.Span.End))
    {
        ExternKeyword = externKeyword;
        StorageClassKeyword = storageClassKeyword;
        MutabilityKeyword = mutabilityKeyword;
        Name = name;
        Colon = colon;
        Type = type;
        EqualsToken = equalsToken;
        Initializer = initializer;
        AtClause = atClause;
        AlignClause = alignClause;
        Semicolon = semicolon;
    }
}

public sealed class TypeAliasDeclarationSyntax : MemberSyntax
{
    public Token ConstKeyword { get; }
    public Token Name { get; }
    public Token EqualsToken { get; }
    public TypeSyntax Type { get; }
    public Token Semicolon { get; }

    public TypeAliasDeclarationSyntax(Token constKeyword, Token name, Token equalsToken, TypeSyntax type, Token semicolon)
        : base(TextSpan.FromBounds(constKeyword.Span.Start, semicolon.Span.End))
    {
        ConstKeyword = constKeyword;
        Name = name;
        EqualsToken = equalsToken;
        Type = type;
        Semicolon = semicolon;
    }
}

public sealed class GlobalStatementSyntax : MemberSyntax
{
    public StatementSyntax Statement { get; }

    public GlobalStatementSyntax(StatementSyntax statement)
        : base(statement.Span)
    {
        Statement = statement;
    }
}
