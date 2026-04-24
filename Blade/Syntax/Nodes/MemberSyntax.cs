using System;
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
/// (name, parameters, return spec). Implemented by <see cref="FunctionDeclarationSyntax"/>,
/// <see cref="AsmFunctionDeclarationSyntax"/>, and <see cref="TaskDeclarationSyntax"/>.
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

public sealed class FunctionDeclarationSyntax(Token? storageClassKeyword, IReadOnlyList<Token> modifiers, Token fnKeyword, Token name, Token openParen,
                                  SeparatedSyntaxList<ParameterSyntax> parameters, Token closeParen,
                                  Token? arrow, SeparatedSyntaxList<ReturnItemSyntax>? returnSpec,
                                  FunctionMetadataSyntax? metadata,
                                  BlockStatementSyntax body) : MemberSyntax(TextSpan.FromBounds([storageClassKeyword, ..modifiers, fnKeyword, Requires.NotNull(body)])), IFunctionSignatureSyntax
{
    public Token? StorageClassKeyword { get; } = storageClassKeyword;
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
    public FunctionMetadataSyntax? Metadata { get; } = metadata;
    public BlockStatementSyntax Body { get; } = Requires.NotNull(body);
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

public sealed class AsmFunctionDeclarationSyntax(Token? storageClassKeyword, IReadOnlyList<Token> modifiers, Token asmKeyword, Token? volatileKeyword, Token fnKeyword, Token name,
                                    Token openParen, SeparatedSyntaxList<ParameterSyntax> parameters, Token closeParen,
                                    Token? arrow, SeparatedSyntaxList<ReturnItemSyntax>? returnSpec,
                                    FunctionMetadataSyntax? metadata,
                                    InlineAsmBodySyntax body) : MemberSyntax(TextSpan.FromBounds([storageClassKeyword, ..modifiers, asmKeyword, volatileKeyword, fnKeyword, body])), IFunctionSignatureSyntax
{
    public Token? StorageClassKeyword { get; } = storageClassKeyword;
    public IReadOnlyList<Token> Modifiers { get; } = Requires.NotNull(modifiers);
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
    public FunctionMetadataSyntax? Metadata { get; } = metadata;
    public InlineAsmBodySyntax Body { get; } = body;
}

/// <summary>
/// Syntax node for a top-level layout declaration.
/// </summary>
public sealed class LayoutDeclarationSyntax(
    Token layoutKeyword,
    Token name,
    Token? colon,
    SeparatedSyntaxList<TypeSyntax>? parentLayouts,
    Token openBrace,
    IReadOnlyList<VariableDeclarationSyntax> declarations,
    Token closeBrace) : MemberSyntax(TextSpan.FromBounds(layoutKeyword.Span.Start, closeBrace.Span.End))
{
    /// <summary>
    /// Gets the <c>layout</c> keyword token.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public Token LayoutKeyword { get; } = layoutKeyword;

    /// <summary>
    /// Gets the identifier token that names the layout.
    /// </summary>
    public Token Name { get; } = name;

    /// <summary>
    /// Gets the colon token that introduces the parent-layout list, when present.
    /// </summary>
    public Token? Colon { get; } = colon;

    /// <summary>
    /// Gets the parent layouts named by this declaration, when present.
    /// </summary>
    public SeparatedSyntaxList<TypeSyntax>? ParentLayouts { get; } = parentLayouts;

    /// <summary>
    /// Gets the opening brace token of the layout body.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public Token OpenBrace { get; } = openBrace;

    /// <summary>
    /// Gets the storage declarations contained in the layout body.
    /// </summary>
    public IReadOnlyList<VariableDeclarationSyntax> Declarations { get; } = Requires.NotNull(declarations);

    /// <summary>
    /// Gets the closing brace token of the layout body.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public Token CloseBrace { get; } = closeBrace;
}

/// <summary>
/// Syntax node for a top-level task declaration.
/// </summary>
public sealed class TaskDeclarationSyntax(
    Token storageClassKeyword,
    Token taskKeyword,
    Token name,
    Token? openParen,
    ParameterSyntax? parameter,
    Token? closeParen,
    Token? colon,
    SeparatedSyntaxList<TypeSyntax>? parentLayouts,
    TaskBodySyntax body) : MemberSyntax(TextSpan.FromBounds(storageClassKeyword.Span.Start, Requires.NotNull(body).Span.End)), IFunctionSignatureSyntax
{
    /// <summary>
    /// Gets the storage-class token that declares where the task executes.
    /// </summary>
    public Token StorageClassKeyword { get; } = storageClassKeyword;

    /// <summary>
    /// Gets the <c>task</c> keyword token.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public Token TaskKeyword { get; } = taskKeyword;

    /// <summary>
    /// Gets the identifier token that names the task.
    /// </summary>
    public Token Name { get; } = name;

    /// <summary>
    /// Gets the opening parenthesis token of the optional startup-parameter list.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public Token? OpenParen { get; } = openParen;

    /// <summary>
    /// Gets the optional startup parameter declared by the task.
    /// </summary>
    public ParameterSyntax? Parameter { get; } = parameter;

    /// <summary>
    /// Gets the task startup parameter in signature-list form.
    /// </summary>
    public SeparatedSyntaxList<ParameterSyntax> Parameters { get; } = parameter is null
        ? new SeparatedSyntaxList<ParameterSyntax>([])
        : new SeparatedSyntaxList<ParameterSyntax>([parameter]);

    /// <summary>
    /// Gets the task return-arrow token, which is always absent because tasks do not declare return values.
    /// </summary>
    public Token? Arrow => null;

    /// <summary>
    /// Gets the task return specification, which is always absent because tasks do not declare return values.
    /// </summary>
    public SeparatedSyntaxList<ReturnItemSyntax>? ReturnSpec => null;

    /// <summary>
    /// Gets the closing parenthesis token of the optional startup-parameter list.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public Token? CloseParen { get; } = closeParen;

    /// <summary>
    /// Gets the colon token that introduces the parent-layout list, when present.
    /// </summary>
    public Token? Colon { get; } = colon;

    /// <summary>
    /// Gets the layouts imported into this task, when present.
    /// </summary>
    public SeparatedSyntaxList<TypeSyntax>? ParentLayouts { get; } = parentLayouts;

    /// <summary>
    /// Gets the mixed declaration-and-statement body of the task.
    /// </summary>
    public TaskBodySyntax Body { get; } = body;
}

public sealed class GlobalStatementSyntax(StatementSyntax statement) : MemberSyntax(Requires.NotNull(statement).Span)
{
    public StatementSyntax Statement { get; } = Requires.NotNull(statement);
}
