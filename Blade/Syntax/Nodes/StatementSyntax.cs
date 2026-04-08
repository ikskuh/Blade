using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Blade;
using Blade.Source;

namespace Blade.Syntax.Nodes;

/// <summary>
/// Base class for all statement nodes.
/// </summary>
public abstract class StatementSyntax : SyntaxNode
{
    protected StatementSyntax(TextSpan span) : base(span) { }
}

public sealed class BlockStatementSyntax : StatementSyntax
{
    [ExcludeFromCodeCoverage]
    public Token OpenBrace { get; }
    public IReadOnlyList<StatementSyntax> Statements { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseBrace { get; }

    public BlockStatementSyntax(Token openBrace, IReadOnlyList<StatementSyntax> statements, Token closeBrace)
        : base(TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End))
    {
        OpenBrace = openBrace;
        Statements = Requires.NotNull(statements);
        CloseBrace = closeBrace;
    }
}

public sealed class VariableDeclarationStatementSyntax : StatementSyntax
{
    public VariableDeclarationSyntax Declaration { get; }

    public VariableDeclarationStatementSyntax(VariableDeclarationSyntax declaration)
        : base(Requires.NotNull(declaration).Span)
    {
        Declaration = Requires.NotNull(declaration);
    }
}

public sealed class ExpressionStatementSyntax : StatementSyntax
{
    public ExpressionSyntax Expression { get; }
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; }

    public ExpressionStatementSyntax(ExpressionSyntax expression, Token semicolon)
        : base(TextSpan.FromBounds(Requires.NotNull(expression).Span.Start, semicolon.Span.End))
    {
        Expression = Requires.NotNull(expression);
        Semicolon = semicolon;
    }
}

public sealed class AssignmentStatementSyntax : StatementSyntax
{
    public ExpressionSyntax Target { get; }
    public Token Operator { get; }
    public ExpressionSyntax Value { get; }
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; }

    public AssignmentStatementSyntax(ExpressionSyntax target, Token @operator, ExpressionSyntax value, Token semicolon)
        : base(TextSpan.FromBounds(Requires.NotNull(target).Span.Start, semicolon.Span.End))
    {
        Target = Requires.NotNull(target);
        Operator = @operator;
        Value = Requires.NotNull(value);
        Semicolon = semicolon;
    }
}

public sealed class MultiAssignmentStatementSyntax : StatementSyntax
{
    public SeparatedSyntaxList<ExpressionSyntax> Targets { get; }
    public Token Operator { get; }
    public ExpressionSyntax Value { get; }
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; }

    public MultiAssignmentStatementSyntax(SeparatedSyntaxList<ExpressionSyntax> targets, Token @operator,
                                           ExpressionSyntax value, Token semicolon)
        : base(TextSpan.FromBounds(Requires.NotNull(targets)[0].Span.Start, semicolon.Span.End))
    {
        Targets = targets;
        Operator = @operator;
        Value = Requires.NotNull(value);
        Semicolon = semicolon;
    }
}

public sealed class IfStatementSyntax : StatementSyntax
{
    [ExcludeFromCodeCoverage]
    public Token IfKeyword { get; }
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; }
    public ExpressionSyntax Condition { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; }
    public StatementSyntax ThenBody { get; }
    public ElseClauseSyntax? ElseClause { get; }

    public IfStatementSyntax(Token ifKeyword, Token openParen, ExpressionSyntax condition, Token closeParen,
                             StatementSyntax thenBody, ElseClauseSyntax? elseClause)
        : base(TextSpan.FromBounds(
            ifKeyword.Span.Start,
            elseClause?.Span.End ?? Requires.NotNull(thenBody).Span.End))
    {
        IfKeyword = ifKeyword;
        OpenParen = openParen;
        Condition = Requires.NotNull(condition);
        CloseParen = closeParen;
        ThenBody = Requires.NotNull(thenBody);
        ElseClause = elseClause;
    }
}

public sealed class WhileStatementSyntax : StatementSyntax
{
    [ExcludeFromCodeCoverage]
    public Token WhileKeyword { get; }
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; }
    public ExpressionSyntax Condition { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; }
    public BlockStatementSyntax Body { get; }

    public WhileStatementSyntax(Token whileKeyword, Token openParen, ExpressionSyntax condition, Token closeParen, BlockStatementSyntax body)
        : base(TextSpan.FromBounds(whileKeyword.Span.Start, Requires.NotNull(body).Span.End))
    {
        WhileKeyword = whileKeyword;
        OpenParen = openParen;
        Condition = Requires.NotNull(condition);
        CloseParen = closeParen;
        Body = Requires.NotNull(body);
    }
}

public sealed class ForStatementSyntax : StatementSyntax
{
    [ExcludeFromCodeCoverage]
    public Token ForKeyword { get; }
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; }
    public ExpressionSyntax Iterable { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; }
    public ForBindingSyntax? Binding { get; }
    public BlockStatementSyntax Body { get; }

    public ForStatementSyntax(Token forKeyword, Token openParen, ExpressionSyntax iterable,
                              Token closeParen, ForBindingSyntax? binding, BlockStatementSyntax body)
        : base(TextSpan.FromBounds(forKeyword.Span.Start, Requires.NotNull(body).Span.End))
    {
        ForKeyword = forKeyword;
        OpenParen = openParen;
        Iterable = Requires.NotNull(iterable);
        CloseParen = closeParen;
        Binding = binding;
        Body = Requires.NotNull(body);
    }
}

public sealed class LoopStatementSyntax : StatementSyntax
{
    [ExcludeFromCodeCoverage]
    public Token LoopKeyword { get; }
    public BlockStatementSyntax Body { get; }

    public LoopStatementSyntax(Token loopKeyword, BlockStatementSyntax body)
        : base(TextSpan.FromBounds(loopKeyword.Span.Start, Requires.NotNull(body).Span.End))
    {
        LoopKeyword = loopKeyword;
        Body = Requires.NotNull(body);
    }
}

public sealed class RepLoopStatementSyntax : StatementSyntax
{
    [ExcludeFromCodeCoverage]
    public Token RepKeyword { get; }
    [ExcludeFromCodeCoverage]
    public Token LoopKeyword { get; }
    public BlockStatementSyntax Body { get; }

    public RepLoopStatementSyntax(Token repKeyword, Token loopKeyword, BlockStatementSyntax body)
        : base(TextSpan.FromBounds(repKeyword.Span.Start, Requires.NotNull(body).Span.End))
    {
        RepKeyword = repKeyword;
        LoopKeyword = loopKeyword;
        Body = Requires.NotNull(body);
    }
}

public sealed class RepForStatementSyntax : StatementSyntax
{
    [ExcludeFromCodeCoverage]
    public Token RepKeyword { get; }
    [ExcludeFromCodeCoverage]
    public Token ForKeyword { get; }
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; }
    public ExpressionSyntax Iterable { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; }
    public ForBindingSyntax? Binding { get; }
    public BlockStatementSyntax Body { get; }

    public RepForStatementSyntax(Token repKeyword, Token forKeyword, Token openParen,
                                  ExpressionSyntax iterable, Token closeParen,
                                  ForBindingSyntax? binding, BlockStatementSyntax body)
        : base(TextSpan.FromBounds(repKeyword.Span.Start, Requires.NotNull(body).Span.End))
    {
        RepKeyword = repKeyword;
        ForKeyword = forKeyword;
        OpenParen = openParen;
        Iterable = Requires.NotNull(iterable);
        CloseParen = closeParen;
        Binding = binding;
        Body = Requires.NotNull(body);
    }
}

public sealed class NoirqStatementSyntax : StatementSyntax
{
    [ExcludeFromCodeCoverage]
    public Token NoirqKeyword { get; }
    public BlockStatementSyntax Body { get; }

    public NoirqStatementSyntax(Token noirqKeyword, BlockStatementSyntax body)
        : base(TextSpan.FromBounds(noirqKeyword.Span.Start, Requires.NotNull(body).Span.End))
    {
        NoirqKeyword = noirqKeyword;
        Body = Requires.NotNull(body);
    }
}

public sealed class AssertStatementSyntax : StatementSyntax
{
    [ExcludeFromCodeCoverage]
    public Token AssertKeyword { get; }
    public ExpressionSyntax Condition { get; }
    public Token? CommaToken { get; }
    public Token? MessageLiteral { get; }
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; }

    public AssertStatementSyntax(Token assertKeyword, ExpressionSyntax condition, Token? commaToken, Token? messageLiteral, Token semicolon)
        : base(TextSpan.FromBounds(assertKeyword.Span.Start, semicolon.Span.End))
    {
        AssertKeyword = assertKeyword;
        Condition = Requires.NotNull(condition);
        CommaToken = commaToken;
        MessageLiteral = messageLiteral;
        Semicolon = semicolon;
    }
}

public sealed class ReturnStatementSyntax : StatementSyntax
{
    [ExcludeFromCodeCoverage]
    public Token ReturnKeyword { get; }
    public SeparatedSyntaxList<ExpressionSyntax>? Values { get; }
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; }

    public ReturnStatementSyntax(Token returnKeyword, SeparatedSyntaxList<ExpressionSyntax>? values, Token semicolon)
        : base(TextSpan.FromBounds(returnKeyword.Span.Start, semicolon.Span.End))
    {
        ReturnKeyword = returnKeyword;
        Values = values;
        Semicolon = semicolon;
    }
}

public sealed class BreakStatementSyntax : StatementSyntax
{
    [ExcludeFromCodeCoverage]
    public Token BreakKeyword { get; }
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; }

    public BreakStatementSyntax(Token breakKeyword, Token semicolon)
        : base(TextSpan.FromBounds(breakKeyword.Span.Start, semicolon.Span.End))
    {
        BreakKeyword = breakKeyword;
        Semicolon = semicolon;
    }
}

public sealed class ContinueStatementSyntax : StatementSyntax
{
    [ExcludeFromCodeCoverage]
    public Token ContinueKeyword { get; }
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; }

    public ContinueStatementSyntax(Token continueKeyword, Token semicolon)
        : base(TextSpan.FromBounds(continueKeyword.Span.Start, semicolon.Span.End))
    {
        ContinueKeyword = continueKeyword;
        Semicolon = semicolon;
    }
}

public sealed class YieldStatementSyntax : StatementSyntax
{
    [ExcludeFromCodeCoverage]
    public Token YieldKeyword { get; }
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; }

    public YieldStatementSyntax(Token yieldKeyword, Token semicolon)
        : base(TextSpan.FromBounds(yieldKeyword.Span.Start, semicolon.Span.End))
    {
        YieldKeyword = yieldKeyword;
        Semicolon = semicolon;
    }
}

public sealed class YieldtoStatementSyntax : StatementSyntax
{
    [ExcludeFromCodeCoverage]
    public Token YieldtoKeyword { get; }
    public Token Target { get; }
    [ExcludeFromCodeCoverage]
    public Token OpenParen { get; }
    public SeparatedSyntaxList<ExpressionSyntax> Arguments { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseParen { get; }
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; }

    public YieldtoStatementSyntax(Token yieldtoKeyword, Token target, Token openParen,
                                   SeparatedSyntaxList<ExpressionSyntax> arguments, Token closeParen, Token semicolon)
        : base(TextSpan.FromBounds(yieldtoKeyword.Span.Start, semicolon.Span.End))
    {
        YieldtoKeyword = yieldtoKeyword;
        Target = target;
        OpenParen = openParen;
        Arguments = arguments;
        CloseParen = closeParen;
        Semicolon = semicolon;
    }
}

public sealed class AsmBlockStatementSyntax : StatementSyntax
{
    [ExcludeFromCodeCoverage]
    public Token AsmKeyword { get; }
    public Token? VolatileKeyword { get; }
    public InlineAsmBodySyntax Body { get; }
    public AsmOutputBindingSyntax? OutputBinding { get; }
    [ExcludeFromCodeCoverage]
    public Token Semicolon { get; }
    public AsmVolatility Volatility => VolatileKeyword is null ? AsmVolatility.NonVolatile : AsmVolatility.Volatile;

    public AsmBlockStatementSyntax(
        Token asmKeyword,
        Token? volatileKeyword,
        InlineAsmBodySyntax body,
        AsmOutputBindingSyntax? outputBinding,
        Token semicolon)
        : base(TextSpan.FromBounds(asmKeyword.Span.Start, semicolon.Span.End))
    {
        AsmKeyword = asmKeyword;
        VolatileKeyword = volatileKeyword;
        Body = Requires.NotNull(body);
        OutputBinding = outputBinding;
        Semicolon = semicolon;
    }
}
