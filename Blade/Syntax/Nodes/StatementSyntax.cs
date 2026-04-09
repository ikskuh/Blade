using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Blade;
using Blade.Source;

namespace Blade.Syntax.Nodes;

/// <summary>
/// Base class for all statement nodes.
/// </summary>
public abstract class StatementSyntax(TextSpan span) : SyntaxNode(span)
{
}

public sealed class BlockStatementSyntax(Token openBrace, IReadOnlyList<StatementSyntax> statements, Token closeBrace) : StatementSyntax(TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token OpenBrace { get; } = openBrace;
    public IReadOnlyList<StatementSyntax> Statements { get; } = Requires.NotNull(statements);
    [ExcludeFromCodeCoverage]
    public Token CloseBrace { get; } = closeBrace;
}

public sealed class VariableDeclarationStatementSyntax(VariableDeclarationSyntax declaration) : StatementSyntax(Requires.NotNull(declaration).Span)
{
    public VariableDeclarationSyntax Declaration { get; } = Requires.NotNull(declaration);
}

public sealed class ExpressionStatementSyntax(ExpressionSyntax expression, Token semicolon) : StatementSyntax(TextSpan.FromBounds(Requires.NotNull(expression).Span.Start, semicolon.Span.End))
{
    public ExpressionSyntax Expression { get; } = Requires.NotNull(expression);
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; } = semicolon;
}

public sealed class AssignmentStatementSyntax(ExpressionSyntax target, Token @operator, ExpressionSyntax value, Token semicolon) : StatementSyntax(TextSpan.FromBounds(Requires.NotNull(target).Span.Start, semicolon.Span.End))
{
    public ExpressionSyntax Target { get; } = Requires.NotNull(target);
    public Token Operator { get; } = @operator;
    public ExpressionSyntax Value { get; } = Requires.NotNull(value);
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; } = semicolon;
}

public sealed class MultiAssignmentStatementSyntax(SeparatedSyntaxList<ExpressionSyntax> targets, Token @operator,
                                       ExpressionSyntax value, Token semicolon) : StatementSyntax(TextSpan.FromBounds(Requires.NotNull(targets)[0].Span.Start, semicolon.Span.End))
{
    public SeparatedSyntaxList<ExpressionSyntax> Targets { get; } = targets;
    public Token Operator { get; } = @operator;
    public ExpressionSyntax Value { get; } = Requires.NotNull(value);
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; } = semicolon;
}

public sealed class IfStatementSyntax(Token ifKeyword, Token openParen, ExpressionSyntax condition, Token closeParen,
                         StatementSyntax thenBody, ElseClauseSyntax? elseClause) : StatementSyntax(TextSpan.FromBounds(
            ifKeyword.Span.Start,
            elseClause?.Span.End ?? Requires.NotNull(thenBody).Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token IfKeyword { get; } = ifKeyword;
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; } = openParen;
    public ExpressionSyntax Condition { get; } = Requires.NotNull(condition);
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; } = closeParen;
    public StatementSyntax ThenBody { get; } = Requires.NotNull(thenBody);
    public ElseClauseSyntax? ElseClause { get; } = elseClause;
}

public sealed class WhileStatementSyntax(Token whileKeyword, Token openParen, ExpressionSyntax condition, Token closeParen, BlockStatementSyntax body) : StatementSyntax(TextSpan.FromBounds(whileKeyword.Span.Start, Requires.NotNull(body).Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token WhileKeyword { get; } = whileKeyword;
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; } = openParen;
    public ExpressionSyntax Condition { get; } = Requires.NotNull(condition);
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; } = closeParen;
    public BlockStatementSyntax Body { get; } = Requires.NotNull(body);
}

public sealed class ForStatementSyntax(Token forKeyword, Token openParen, ExpressionSyntax iterable,
                          Token closeParen, ForBindingSyntax? binding, BlockStatementSyntax body) : StatementSyntax(TextSpan.FromBounds(forKeyword.Span.Start, Requires.NotNull(body).Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token ForKeyword { get; } = forKeyword;
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; } = openParen;
    public ExpressionSyntax Iterable { get; } = Requires.NotNull(iterable);
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; } = closeParen;
    public ForBindingSyntax? Binding { get; } = binding;
    public BlockStatementSyntax Body { get; } = Requires.NotNull(body);
}

public sealed class LoopStatementSyntax(Token loopKeyword, BlockStatementSyntax body) : StatementSyntax(TextSpan.FromBounds(loopKeyword.Span.Start, Requires.NotNull(body).Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token LoopKeyword { get; } = loopKeyword;
    public BlockStatementSyntax Body { get; } = Requires.NotNull(body);
}

public sealed class RepLoopStatementSyntax(Token repKeyword, Token loopKeyword, BlockStatementSyntax body) : StatementSyntax(TextSpan.FromBounds(repKeyword.Span.Start, Requires.NotNull(body).Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token RepKeyword { get; } = repKeyword;
    [ExcludeFromCodeCoverage]
    public Token LoopKeyword { get; } = loopKeyword;
    public BlockStatementSyntax Body { get; } = Requires.NotNull(body);
}

public sealed class RepForStatementSyntax(Token repKeyword, Token forKeyword, Token openParen,
                              ExpressionSyntax iterable, Token closeParen,
                              ForBindingSyntax? binding, BlockStatementSyntax body) : StatementSyntax(TextSpan.FromBounds(repKeyword.Span.Start, Requires.NotNull(body).Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token RepKeyword { get; } = repKeyword;
    [ExcludeFromCodeCoverage]
    public Token ForKeyword { get; } = forKeyword;
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; } = openParen;
    public ExpressionSyntax Iterable { get; } = Requires.NotNull(iterable);
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; } = closeParen;
    public ForBindingSyntax? Binding { get; } = binding;
    public BlockStatementSyntax Body { get; } = Requires.NotNull(body);
}

public sealed class NoirqStatementSyntax(Token noirqKeyword, BlockStatementSyntax body) : StatementSyntax(TextSpan.FromBounds(noirqKeyword.Span.Start, Requires.NotNull(body).Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token NoirqKeyword { get; } = noirqKeyword;
    public BlockStatementSyntax Body { get; } = Requires.NotNull(body);
}

public sealed class AssertStatementSyntax(Token assertKeyword, ExpressionSyntax condition, Token? commaToken, Token? messageLiteral, Token semicolon) : StatementSyntax(TextSpan.FromBounds(assertKeyword.Span.Start, semicolon.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token AssertKeyword { get; } = assertKeyword;
    public ExpressionSyntax Condition { get; } = Requires.NotNull(condition);
    public Token? CommaToken { get; } = commaToken;
    public Token? MessageLiteral { get; } = messageLiteral;
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; } = semicolon;
}

public sealed class ReturnStatementSyntax(Token returnKeyword, SeparatedSyntaxList<ExpressionSyntax>? values, Token semicolon) : StatementSyntax(TextSpan.FromBounds(returnKeyword.Span.Start, semicolon.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token ReturnKeyword { get; } = returnKeyword;
    public SeparatedSyntaxList<ExpressionSyntax>? Values { get; } = values;
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; } = semicolon;
}

public sealed class BreakStatementSyntax(Token breakKeyword, Token semicolon) : StatementSyntax(TextSpan.FromBounds(breakKeyword.Span.Start, semicolon.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token BreakKeyword { get; } = breakKeyword;
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; } = semicolon;
}

public sealed class ContinueStatementSyntax(Token continueKeyword, Token semicolon) : StatementSyntax(TextSpan.FromBounds(continueKeyword.Span.Start, semicolon.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token ContinueKeyword { get; } = continueKeyword;
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; } = semicolon;
}

public sealed class YieldStatementSyntax(Token yieldKeyword, Token semicolon) : StatementSyntax(TextSpan.FromBounds(yieldKeyword.Span.Start, semicolon.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token YieldKeyword { get; } = yieldKeyword;
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; } = semicolon;
}

public sealed class YieldtoStatementSyntax(Token yieldtoKeyword, Token target, Token openParen,
                               SeparatedSyntaxList<ExpressionSyntax> arguments, Token closeParen, Token semicolon) : StatementSyntax(TextSpan.FromBounds(yieldtoKeyword.Span.Start, semicolon.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token YieldtoKeyword { get; } = yieldtoKeyword;
    public Token Target { get; } = target;
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; } = openParen;
    public SeparatedSyntaxList<ExpressionSyntax> Arguments { get; } = arguments;
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; } = closeParen;
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; } = semicolon;
}

public sealed class AsmBlockStatementSyntax(
    Token asmKeyword,
    Token? volatileKeyword,
    InlineAsmBodySyntax body,
    AsmOutputBindingSyntax? outputBinding,
    Token semicolon) : StatementSyntax(TextSpan.FromBounds(asmKeyword.Span.Start, semicolon.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token AsmKeyword { get; } = asmKeyword;
    public Token? VolatileKeyword { get; } = volatileKeyword;
    public InlineAsmBodySyntax Body { get; } = Requires.NotNull(body);
    public AsmOutputBindingSyntax? OutputBinding { get; } = outputBinding;
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; } = semicolon;
    public AsmVolatility Volatility => VolatileKeyword is null ? AsmVolatility.NonVolatile : AsmVolatility.Volatile;
}
