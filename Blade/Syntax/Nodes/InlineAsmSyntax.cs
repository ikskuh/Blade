using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Blade.Source;

namespace Blade.Syntax.Nodes;

/// <summary>
/// Structured inline-assembly body between `{` and `}`. Produced by the parser
/// so that downstream stages never need to re-parse raw asm text.
/// </summary>
public sealed class InlineAsmBodySyntax : SyntaxNode
{
    public InlineAsmBodySyntax(Token openBrace, IReadOnlyList<InlineAsmLineSyntax> lines, Token closeBrace)
        : base(TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End))
    {
        OpenBrace = openBrace;
        Lines = Requires.NotNull(lines);
        CloseBrace = closeBrace;
    }

    [ExcludeFromCodeCoverage]
    public Token OpenBrace { get; }
    public IReadOnlyList<InlineAsmLineSyntax> Lines { get; }
    [ExcludeFromCodeCoverage]
    public Token CloseBrace { get; }
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

public sealed class InlineAsmLabelLineSyntax : InlineAsmLineSyntax
{
    public InlineAsmLabelLineSyntax(Token name, Token colon, string? trailingComment)
        : base(TextSpan.FromBounds(name.Span.Start, colon.Span.End), trailingComment)
    {
        Name = name;
        Colon = colon;
    }

    public Token Name { get; }
    [ExcludeFromCodeCoverage]
    public Token Colon { get; }
}

public sealed class InlineAsmInstructionLineSyntax : InlineAsmLineSyntax
{
    public InlineAsmInstructionLineSyntax(
        Token? condition,
        Token mnemonic,
        IReadOnlyList<InlineAsmOperandSyntax> operands,
        Token? flagEffect,
        string? trailingComment)
        : base(ComputeSpan(condition, mnemonic, Requires.NotNull(operands), flagEffect), trailingComment)
    {
        Condition = condition;
        Mnemonic = mnemonic;
        Operands = Requires.NotNull(operands);
        FlagEffect = flagEffect;
    }

    public Token? Condition { get; }
    public Token Mnemonic { get; }
    public IReadOnlyList<InlineAsmOperandSyntax> Operands { get; }
    public Token? FlagEffect { get; }

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

public sealed class InlineAsmVarBindingOperandSyntax : InlineAsmOperandSyntax
{
    public InlineAsmVarBindingOperandSyntax(Token openBrace, IReadOnlyList<Token> path, Token closeBrace)
        : base(TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End))
    {
        OpenBrace = openBrace;
        Path = Requires.NotNull(path);
        CloseBrace = closeBrace;
    }

    [ExcludeFromCodeCoverage]
    public Token OpenBrace { get; }
    public IReadOnlyList<Token> Path { get; }
    public Token Name => Path[0];
    [ExcludeFromCodeCoverage]
    public Token CloseBrace { get; }
}

public sealed class InlineAsmTempBindingOperandSyntax : InlineAsmOperandSyntax
{
    public InlineAsmTempBindingOperandSyntax(Token percent, Token number)
        : base(TextSpan.FromBounds(percent.Span.Start, number.Span.End))
    {
        Percent = percent;
        Number = number;
    }

    [ExcludeFromCodeCoverage]
    public Token Percent { get; }
    public Token Number { get; }
}

public sealed class InlineAsmImmediateOperandSyntax : InlineAsmOperandSyntax
{
    public InlineAsmImmediateOperandSyntax(Token hash, InlineAsmOperandSyntax inner)
        : base(TextSpan.FromBounds(hash.Span.Start, Requires.NotNull(inner).Span.End))
    {
        Hash = hash;
        Inner = inner;
    }

    [ExcludeFromCodeCoverage]
    public Token Hash { get; }
    public InlineAsmOperandSyntax Inner { get; }
}

public sealed class InlineAsmIntegerLiteralOperandSyntax : InlineAsmOperandSyntax
{
    public InlineAsmIntegerLiteralOperandSyntax(Token? sign, Token literal)
        : base(TextSpan.FromBounds(sign?.Span.Start ?? literal.Span.Start, literal.Span.End))
    {
        Sign = sign;
        Literal = literal;
    }

    public Token? Sign { get; }
    public Token Literal { get; }
}

public sealed class InlineAsmCurrentAddressOperandSyntax(Token dollar) : InlineAsmOperandSyntax(dollar.Span)
{
    public Token Dollar { get; } = dollar;
}

public sealed class InlineAsmSymbolOperandSyntax(Token name) : InlineAsmOperandSyntax(name.Span)
{
    public Token Name { get; } = name;
}
