using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Blade.Source;

namespace Blade.Syntax.Nodes;

/// <summary>
/// Structured inline-assembly body between `{` and `}`. Produced by the parser
/// so that downstream stages never need to re-parse raw asm text.
/// </summary>
public sealed class InlineAsmBodySyntax(Token openBrace, IReadOnlyList<InlineAsmLineSyntax> lines, Token closeBrace) : SyntaxNode(TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token OpenBrace { get; } = openBrace;
    public IReadOnlyList<InlineAsmLineSyntax> Lines { get; } = Requires.NotNull(lines);
    [ExcludeFromCodeCoverage]
    public Token CloseBrace { get; } = closeBrace;
}

public abstract class InlineAsmLineSyntax(TextSpan span, string? trailingComment) : SyntaxNode(span)
{
    /// <summary>
    /// Trailing `// …` comment text on the same physical line (already trimmed of the leading `//`).
    /// Preserved for dump/codegen output; has no semantic meaning.
    /// </summary>
    public string? TrailingComment { get; } = trailingComment;
}

/// <summary>
/// Standalone comment occupying its own physical line inside the asm body.
/// </summary>
public sealed class InlineAsmCommentLineSyntax(TextSpan span, string comment)
    : InlineAsmLineSyntax(span, null)
{
    public string Comment { get; } = Requires.NotNull(comment);
}

public sealed class InlineAsmLabelLineSyntax(Token name, Token colon, string? trailingComment) : InlineAsmLineSyntax(TextSpan.FromBounds(name.Span.Start, colon.Span.End), trailingComment)
{
    public Token Name { get; } = name;
    [ExcludeFromCodeCoverage]
    public Token Colon { get; } = colon;
}

public sealed class InlineAsmInstructionLineSyntax(
    Token? condition,
    Token mnemonic,
    IReadOnlyList<InlineAsmOperandSyntax> operands,
    Token? flagEffect,
    string? trailingComment) : InlineAsmLineSyntax(ComputeSpan(condition, mnemonic, Requires.NotNull(operands), flagEffect), trailingComment)
{
    public Token? Condition { get; } = condition;
    public Token Mnemonic { get; } = mnemonic;
    public IReadOnlyList<InlineAsmOperandSyntax> Operands { get; } = Requires.NotNull(operands);
    public Token? FlagEffect { get; } = flagEffect;

    private static TextSpan ComputeSpan(Token? condition, Token mnemonic, IReadOnlyList<InlineAsmOperandSyntax> operands, Token? flagEffect)
    {
        int start = condition?.Span.Start ?? mnemonic.Span.Start;
        int end = mnemonic.Span.End;
        if (operands.Count > 0)
            end = operands[^1].Span.End;
        if (flagEffect is Token fe)
            end = fe.Span.End;
        return TextSpan.FromBounds(start, end);
    }
}

public abstract class InlineAsmOperandSyntax(TextSpan span) : SyntaxNode(span)
{
}

public sealed class InlineAsmVarBindingOperandSyntax(Token openBrace, IReadOnlyList<Token> path, Token closeBrace) : InlineAsmOperandSyntax(TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token OpenBrace { get; } = openBrace;
    public IReadOnlyList<Token> Path { get; } = Requires.NotNull(path);
    [ExcludeFromCodeCoverage]
    public Token CloseBrace { get; } = closeBrace;
}

public sealed class InlineAsmTempBindingOperandSyntax(Token percent, Token number) : InlineAsmOperandSyntax(TextSpan.FromBounds(percent.Span.Start, number.Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token Percent { get; } = percent;
    public Token Number { get; } = number;
}

public sealed class InlineAsmImmediateOperandSyntax(Token hash, InlineAsmOperandSyntax inner) : InlineAsmOperandSyntax(TextSpan.FromBounds(hash.Span.Start, Requires.NotNull(inner).Span.End))
{
    [ExcludeFromCodeCoverage]
    public Token Hash { get; } = hash;
    public InlineAsmOperandSyntax Inner { get; } = inner;
}

public sealed class InlineAsmIntegerLiteralOperandSyntax(Token? sign, Token literal) : InlineAsmOperandSyntax(TextSpan.FromBounds(sign?.Span.Start ?? literal.Span.Start, literal.Span.End))
{
    public Token? Sign { get; } = sign;
    public Token Literal { get; } = literal;
}

public sealed class InlineAsmCurrentAddressOperandSyntax(Token dollar) : InlineAsmOperandSyntax(dollar.Span)
{
    public Token Dollar { get; } = dollar;
}

public sealed class InlineAsmSymbolOperandSyntax(Token name) : InlineAsmOperandSyntax(name.Span)
{
    public Token Name { get; } = name;
}
