using System.Collections.Generic;
using Blade.Source;
using Blade.Semantics;
using Blade.Syntax;

namespace Blade.Semantics.Bound;

public abstract class BoundExpression : BoundNode
{
    protected BoundExpression(BoundNodeKind kind, TextSpan span, BladeType type)
        : base(kind, span)
    {
        Type = type;
    }

    public BladeType Type { get; }
}

public sealed class BoundLiteralExpression : BoundExpression
{
    public BoundLiteralExpression(BladeValue value, TextSpan span)
        : base(BoundNodeKind.LiteralExpression, span, Requires.NotNull(value).Type)
    {
        Value = Requires.NotNull(value);
    }

    public BladeValue Value { get; }
}

public sealed class BoundSymbolExpression : BoundExpression
{
    public BoundSymbolExpression(Symbol symbol, TextSpan span, BladeType type)
        : base(BoundNodeKind.SymbolExpression, span, type)
    {
        Symbol = symbol;
    }

    public Symbol Symbol { get; }
}

public enum BoundUnaryOperatorKind
{
    LogicalNot,
    Negation,
    BitwiseNot,
    UnaryPlus,
    AddressOf,
}

public sealed class BoundUnaryOperator
{
    public BoundUnaryOperator(TokenKind syntaxKind, BoundUnaryOperatorKind kind)
    {
        SyntaxKind = syntaxKind;
        Kind = kind;
    }

    public TokenKind SyntaxKind { get; }
    public BoundUnaryOperatorKind Kind { get; }

    public static BoundUnaryOperator? Bind(TokenKind kind)
    {
        return kind switch
        {
            TokenKind.Bang => new BoundUnaryOperator(kind, BoundUnaryOperatorKind.LogicalNot),
            TokenKind.Minus => new BoundUnaryOperator(kind, BoundUnaryOperatorKind.Negation),
            TokenKind.Tilde => new BoundUnaryOperator(kind, BoundUnaryOperatorKind.BitwiseNot),
            TokenKind.Plus => new BoundUnaryOperator(kind, BoundUnaryOperatorKind.UnaryPlus),
            TokenKind.Ampersand => new BoundUnaryOperator(kind, BoundUnaryOperatorKind.AddressOf),
            _ => null,
        };
    }
}

public sealed class BoundUnaryExpression : BoundExpression
{
    public BoundUnaryExpression(BoundUnaryOperator op, BoundExpression operand, TextSpan span, BladeType type)
        : base(BoundNodeKind.UnaryExpression, span, type)
    {
        Operator = op;
        Operand = operand;
    }

    public BoundUnaryOperator Operator { get; }
    public BoundExpression Operand { get; }
}

public enum BoundBinaryOperatorKind
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    BitwiseAnd,
    BitwiseOr,
    BitwiseXor,
    ShiftLeft,
    ShiftRight,
    ArithmeticShiftLeft,
    ArithmeticShiftRight,
    RotateLeft,
    RotateRight,
    LogicalAnd,
    LogicalOr,
    Equals,
    NotEquals,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

public sealed class BoundBinaryOperator
{
    private BoundBinaryOperator(TokenKind syntaxKind, BoundBinaryOperatorKind kind, bool isComparison)
    {
        SyntaxKind = syntaxKind;
        Kind = kind;
        IsComparison = isComparison;
    }

    public TokenKind SyntaxKind { get; }
    public BoundBinaryOperatorKind Kind { get; }
    public bool IsComparison { get; }

    public static BoundBinaryOperator? Bind(TokenKind kind)
    {
        return kind switch
        {
            TokenKind.Plus => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.Add, isComparison: false),
            TokenKind.Minus => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.Subtract, isComparison: false),
            TokenKind.Star => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.Multiply, isComparison: false),
            TokenKind.Slash => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.Divide, isComparison: false),
            TokenKind.Percent => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.Modulo, isComparison: false),
            TokenKind.Ampersand => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.BitwiseAnd, isComparison: false),
            TokenKind.Pipe => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.BitwiseOr, isComparison: false),
            TokenKind.Caret => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.BitwiseXor, isComparison: false),
            TokenKind.LessLess => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.ShiftLeft, isComparison: false),
            TokenKind.GreaterGreater => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.ShiftRight, isComparison: false),
            TokenKind.LessLessLess => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.ArithmeticShiftLeft, isComparison: false),
            TokenKind.GreaterGreaterGreater => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.ArithmeticShiftRight, isComparison: false),
            TokenKind.RotateLeft => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.RotateLeft, isComparison: false),
            TokenKind.RotateRight => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.RotateRight, isComparison: false),
            TokenKind.AndKeyword => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.LogicalAnd, isComparison: false),
            TokenKind.OrKeyword => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.LogicalOr, isComparison: false),
            TokenKind.EqualEqual => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.Equals, isComparison: true),
            TokenKind.BangEqual => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.NotEquals, isComparison: true),
            TokenKind.Less => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.Less, isComparison: true),
            TokenKind.LessEqual => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.LessOrEqual, isComparison: true),
            TokenKind.Greater => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.Greater, isComparison: true),
            TokenKind.GreaterEqual => new BoundBinaryOperator(kind, BoundBinaryOperatorKind.GreaterOrEqual, isComparison: true),
            _ => null,
        };
    }
}

public sealed class BoundBinaryExpression : BoundExpression
{
    public BoundBinaryExpression(BoundExpression left, BoundBinaryOperator op, BoundExpression right, TextSpan span, BladeType type)
        : base(BoundNodeKind.BinaryExpression, span, type)
    {
        Left = left;
        Operator = op;
        Right = right;
    }

    public BoundExpression Left { get; }
    public BoundBinaryOperator Operator { get; }
    public BoundExpression Right { get; }
}

public sealed class BoundCallExpression : BoundExpression
{
    public BoundCallExpression(FunctionSymbol function, IReadOnlyList<BoundExpression> arguments, TextSpan span, BladeType type)
        : base(BoundNodeKind.CallExpression, span, type)
    {
        Function = function;
        Arguments = arguments;
    }

    public FunctionSymbol Function { get; }
    public IReadOnlyList<BoundExpression> Arguments { get; }
}


public sealed class BoundModuleCallExpression : BoundExpression
{
    public BoundModuleCallExpression(BoundModule module, TextSpan span)
        : base(BoundNodeKind.ModuleCallExpression, span, BuiltinTypes.Void)
    {
        Module = Requires.NotNull(module);
    }

    public BoundModule Module { get; }
}

public sealed class BoundIntrinsicCallExpression : BoundExpression
{
    public BoundIntrinsicCallExpression(string name, IReadOnlyList<BoundExpression> arguments, TextSpan span, BladeType type)
        : this(ParseMnemonic(name), arguments, span, type)
    {
    }

    public BoundIntrinsicCallExpression(P2Mnemonic mnemonic, IReadOnlyList<BoundExpression> arguments, TextSpan span, BladeType type)
        : base(BoundNodeKind.IntrinsicCallExpression, span, type)
    {
        Mnemonic = mnemonic;
        Arguments = arguments;
    }

    public P2Mnemonic Mnemonic { get; }
    public IReadOnlyList<BoundExpression> Arguments { get; }

    private static P2Mnemonic ParseMnemonic(string name)
    {
        string normalized = Requires.NotNullOrWhiteSpace(name);
        if (normalized.StartsWith('@'))
            normalized = normalized[1..];

        bool parsed = P2InstructionMetadata.TryParseMnemonic(normalized, out P2Mnemonic mnemonic);
        Assert.Invariant(parsed, $"Intrinsic '{name}' must resolve to a valid P2 mnemonic.");
        return mnemonic;
    }
}

/// <summary>
/// Only required inside the binder as a sentinel value, not a public api.
/// </summary>
internal sealed class BoundEnumLiteralExpression : BoundExpression
{
    public BoundEnumLiteralExpression(EnumTypeSymbol enumType, string memberName, long value, TextSpan span)
        : base(BoundNodeKind.EnumLiteralExpression, span, enumType)
    {
        EnumType = enumType;
        MemberName = memberName;
        Value = value;
    }

    public EnumTypeSymbol EnumType { get; }
    public string MemberName { get; }
    public long Value { get; }
}

public sealed class BoundArrayLiteralExpression : BoundExpression
{
    public BoundArrayLiteralExpression(IReadOnlyList<BoundExpression> elements, bool lastElementIsSpread, TextSpan span, ArrayTypeSymbol type)
        : base(BoundNodeKind.ArrayLiteralExpression, span, type)
    {
        Elements = Requires.NotNull(elements);
        LastElementIsSpread = lastElementIsSpread;
    }

    public IReadOnlyList<BoundExpression> Elements { get; }
    public bool LastElementIsSpread { get; }
    public new ArrayTypeSymbol Type => (ArrayTypeSymbol)base.Type;
}

public sealed class BoundMemberAccessExpression : BoundExpression
{
    public BoundMemberAccessExpression(BoundExpression receiver, AggregateMemberSymbol member, TextSpan span)
        : base(BoundNodeKind.MemberAccessExpression, span, Requires.NotNull(member).Type)
    {
        Receiver = receiver;
        Member = Requires.NotNull(member);
    }

    public BoundExpression Receiver { get; }
    public AggregateMemberSymbol Member { get; }
    public string MemberName => Member.Name;
}

public sealed class BoundIndexExpression : BoundExpression
{
    public BoundIndexExpression(BoundExpression expression, BoundExpression index, TextSpan span, BladeType type)
        : base(BoundNodeKind.IndexExpression, span, type)
    {
        Expression = expression;
        Index = index;
    }

    public BoundExpression Expression { get; }
    public BoundExpression Index { get; }
}

public sealed class BoundPointerDerefExpression : BoundExpression
{
    public BoundPointerDerefExpression(BoundExpression expression, TextSpan span, BladeType type)
        : base(BoundNodeKind.PointerDerefExpression, span, type)
    {
        Expression = expression;
    }

    public BoundExpression Expression { get; }
}

public sealed class BoundIfExpression : BoundExpression
{
    public BoundIfExpression(BoundExpression condition, BoundExpression thenExpression, BoundExpression elseExpression, TextSpan span, BladeType type)
        : base(BoundNodeKind.IfExpression, span, type)
    {
        Condition = condition;
        ThenExpression = thenExpression;
        ElseExpression = elseExpression;
    }

    public BoundExpression Condition { get; }
    public BoundExpression ThenExpression { get; }
    public BoundExpression ElseExpression { get; }
}

public sealed class BoundRangeExpression : BoundExpression
{
    public BoundRangeExpression(BoundExpression start, BoundExpression end, bool isInclusive, TextSpan span)
        : base(BoundNodeKind.RangeExpression, span, BuiltinTypes.Range)
    {
        Start = start;
        End = end;
        IsInclusive = isInclusive;
    }

    public BoundExpression Start { get; }
    public BoundExpression End { get; }
    public bool IsInclusive { get; }
}

public sealed class BoundStructFieldInitializer
{
    public BoundStructFieldInitializer(string name, BoundExpression value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public BoundExpression Value { get; }
}

public sealed class BoundStructLiteralExpression : BoundExpression
{
    public BoundStructLiteralExpression(IReadOnlyList<BoundStructFieldInitializer> fields, TextSpan span, BladeType type)
        : base(BoundNodeKind.StructLiteralExpression, span, type)
    {
        Fields = fields;
    }

    public IReadOnlyList<BoundStructFieldInitializer> Fields { get; }
}

public sealed class BoundConversionExpression : BoundExpression
{
    public BoundConversionExpression(BoundExpression expression, TextSpan span, BladeType targetType)
        : base(BoundNodeKind.ConversionExpression, span, targetType)
    {
        Expression = expression;
    }

    public BoundExpression Expression { get; }
}

public sealed class BoundCastExpression : BoundExpression
{
    public BoundCastExpression(BoundExpression expression, TextSpan span, BladeType targetType)
        : base(BoundNodeKind.CastExpression, span, targetType)
    {
        Expression = expression;
    }

    public BoundExpression Expression { get; }
}

public sealed class BoundBitcastExpression : BoundExpression
{
    public BoundBitcastExpression(BoundExpression expression, TextSpan span, BladeType targetType)
        : base(BoundNodeKind.BitcastExpression, span, targetType)
    {
        Expression = expression;
    }

    public BoundExpression Expression { get; }
}

/// <summary>
/// Only required inside the binder as a sentinel value, not a public api.
/// </summary>
internal sealed class BoundErrorExpression : BoundExpression
{
    public BoundErrorExpression(TextSpan span)
        : base(BoundNodeKind.ErrorExpression, span, BuiltinTypes.Unknown)
    {
    }
}

public abstract class BoundAssignmentTarget : BoundNode
{
    protected BoundAssignmentTarget(BoundNodeKind kind, TextSpan span, BladeType type)
        : base(kind, span)
    {
        Type = type;
    }

    public BladeType Type { get; }
}

public sealed class BoundSymbolAssignmentTarget : BoundAssignmentTarget
{
    public BoundSymbolAssignmentTarget(Symbol symbol, TextSpan span, BladeType type)
        : base(BoundNodeKind.SymbolAssignmentTarget, span, type)
    {
        Symbol = symbol;
    }

    public Symbol Symbol { get; }
}

public sealed class BoundMemberAssignmentTarget : BoundAssignmentTarget
{
    public BoundMemberAssignmentTarget(BoundExpression receiver, AggregateMemberSymbol member, TextSpan span)
        : base(BoundNodeKind.MemberAssignmentTarget, span, Requires.NotNull(member).Type)
    {
        Receiver = receiver;
        Member = Requires.NotNull(member);
    }

    public BoundExpression Receiver { get; }
    public AggregateMemberSymbol Member { get; }
    public string MemberName => Member.Name;
}

public sealed class BoundBitfieldAssignmentTarget : BoundAssignmentTarget
{
    public BoundBitfieldAssignmentTarget(BoundAssignmentTarget receiverTarget, BoundExpression receiverValue, AggregateMemberSymbol member, TextSpan span)
        : base(BoundNodeKind.BitfieldAssignmentTarget, span, Requires.NotNull(member).Type)
    {
        ReceiverTarget = receiverTarget;
        ReceiverValue = receiverValue;
        Member = Requires.NotNull(member);
    }

    public BoundAssignmentTarget ReceiverTarget { get; }
    public BoundExpression ReceiverValue { get; }
    public AggregateMemberSymbol Member { get; }
}

public sealed class BoundIndexAssignmentTarget : BoundAssignmentTarget
{
    public BoundIndexAssignmentTarget(BoundExpression expression, BoundExpression index, TextSpan span, BladeType type)
        : base(BoundNodeKind.IndexAssignmentTarget, span, type)
    {
        Expression = expression;
        Index = index;
    }

    public BoundExpression Expression { get; }
    public BoundExpression Index { get; }
}

public sealed class BoundPointerDerefAssignmentTarget : BoundAssignmentTarget
{
    public BoundPointerDerefAssignmentTarget(BoundExpression expression, TextSpan span, BladeType type)
        : base(BoundNodeKind.PointerDerefAssignmentTarget, span, type)
    {
        Expression = expression;
    }

    public BoundExpression Expression { get; }
}

public sealed class BoundDiscardAssignmentTarget : BoundAssignmentTarget
{
    public BoundDiscardAssignmentTarget(TextSpan span, BladeType type)
        : base(BoundNodeKind.DiscardAssignmentTarget, span, type)
    {
    }
}

/// <summary>
/// Only required inside the binder as a sentinel value, not a public api.
/// </summary>
internal sealed class BoundErrorAssignmentTarget : BoundAssignmentTarget
{
    public BoundErrorAssignmentTarget(TextSpan span)
        : base(BoundNodeKind.ErrorAssignmentTarget, span, BuiltinTypes.Unknown)
    {
    }
}
