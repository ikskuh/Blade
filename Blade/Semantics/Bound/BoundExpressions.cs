using System.Collections.Generic;
using Blade.Source;
using Blade.Semantics;
using Blade.Syntax;

namespace Blade.Semantics.Bound;

public abstract class BoundExpression(BoundNodeKind kind, TextSpan span, BladeType type) : BoundNode(kind, span)
{
    public BladeType Type { get; } = type;
}

public sealed class BoundLiteralExpression(BladeValue value, TextSpan span) : BoundExpression(BoundNodeKind.LiteralExpression, span, Requires.NotNull(value).Type)
{
    public BladeValue Value { get; } = Requires.NotNull(value);
}

public sealed class BoundSymbolExpression(Symbol symbol, TextSpan span, BladeType type) : BoundExpression(BoundNodeKind.SymbolExpression, span, type)
{
    public Symbol Symbol { get; } = symbol;
}

public enum BoundUnaryOperatorKind
{
    LogicalNot,
    Negation,
    BitwiseNot,
    UnaryPlus,
    AddressOf,
}

public sealed class BoundUnaryOperator(TokenKind syntaxKind, BoundUnaryOperatorKind kind)
{
    public TokenKind SyntaxKind { get; } = syntaxKind;
    public BoundUnaryOperatorKind Kind { get; } = kind;

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

public sealed class BoundUnaryExpression(BoundUnaryOperator op, BoundExpression operand, TextSpan span, BladeType type) : BoundExpression(BoundNodeKind.UnaryExpression, span, type)
{
    public BoundUnaryOperator Operator { get; } = op;
    public BoundExpression Operand { get; } = operand;
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

public sealed class BoundBinaryExpression(BoundExpression left, BoundBinaryOperator op, BoundExpression right, TextSpan span, BladeType type) : BoundExpression(BoundNodeKind.BinaryExpression, span, type)
{
    public BoundExpression Left { get; } = left;
    public BoundBinaryOperator Operator { get; } = op;
    public BoundExpression Right { get; } = right;
}

public sealed class BoundCallExpression(FunctionSymbol function, IReadOnlyList<BoundExpression> arguments, TextSpan span, BladeType type) : BoundExpression(BoundNodeKind.CallExpression, span, type)
{
    public FunctionSymbol Function { get; } = function;
    public IReadOnlyList<BoundExpression> Arguments { get; } = arguments;
}


public sealed class BoundModuleCallExpression(BoundModule module, TextSpan span) : BoundExpression(BoundNodeKind.ModuleCallExpression, span, BuiltinTypes.Void)
{
    public BoundModule Module { get; } = Requires.NotNull(module);
}

public sealed class BoundIntrinsicCallExpression(P2Mnemonic mnemonic, IReadOnlyList<BoundExpression> arguments, TextSpan span, BladeType type) : BoundExpression(BoundNodeKind.IntrinsicCallExpression, span, type)
{
    public BoundIntrinsicCallExpression(string name, IReadOnlyList<BoundExpression> arguments, TextSpan span, BladeType type)
        : this(ParseMnemonic(name), arguments, span, type)
    {
    }

    public P2Mnemonic Mnemonic { get; } = mnemonic;
    public IReadOnlyList<BoundExpression> Arguments { get; } = arguments;

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
internal sealed class BoundEnumLiteralExpression(EnumTypeSymbol enumType, string memberName, long value, TextSpan span) : BoundExpression(BoundNodeKind.EnumLiteralExpression, span, enumType)
{
    public EnumTypeSymbol EnumType { get; } = enumType;
    public string MemberName { get; } = memberName;
    public long Value { get; } = value;
}

public sealed class BoundArrayLiteralExpression(IReadOnlyList<BoundExpression> elements, bool lastElementIsSpread, TextSpan span, ArrayTypeSymbol type) : BoundExpression(BoundNodeKind.ArrayLiteralExpression, span, type)
{
    public IReadOnlyList<BoundExpression> Elements { get; } = Requires.NotNull(elements);
    public bool LastElementIsSpread { get; } = lastElementIsSpread;
    public new ArrayTypeSymbol Type => (ArrayTypeSymbol)base.Type;
}

public sealed class BoundMemberAccessExpression(BoundExpression receiver, AggregateMemberSymbol member, TextSpan span) : BoundExpression(BoundNodeKind.MemberAccessExpression, span, Requires.NotNull(member).Type)
{
    public BoundExpression Receiver { get; } = receiver;
    public AggregateMemberSymbol Member { get; } = Requires.NotNull(member);
    public string MemberName => Member.Name;
}

public sealed class BoundIndexExpression(BoundExpression expression, BoundExpression index, TextSpan span, BladeType type) : BoundExpression(BoundNodeKind.IndexExpression, span, type)
{
    public BoundExpression Expression { get; } = expression;
    public BoundExpression Index { get; } = index;
}

public sealed class BoundPointerDerefExpression(BoundExpression expression, TextSpan span, BladeType type) : BoundExpression(BoundNodeKind.PointerDerefExpression, span, type)
{
    public BoundExpression Expression { get; } = expression;
}

public sealed class BoundIfExpression(BoundExpression condition, BoundExpression thenExpression, BoundExpression elseExpression, TextSpan span, BladeType type) : BoundExpression(BoundNodeKind.IfExpression, span, type)
{
    public BoundExpression Condition { get; } = condition;
    public BoundExpression ThenExpression { get; } = thenExpression;
    public BoundExpression ElseExpression { get; } = elseExpression;
}

public sealed class BoundRangeExpression(BoundExpression start, BoundExpression end, bool isInclusive, TextSpan span) : BoundExpression(BoundNodeKind.RangeExpression, span, BuiltinTypes.Range)
{
    public BoundExpression Start { get; } = start;
    public BoundExpression End { get; } = end;
    public bool IsInclusive { get; } = isInclusive;
}

public sealed class BoundStructFieldInitializer(string name, BoundExpression value)
{
    public string Name { get; } = name;
    public BoundExpression Value { get; } = value;
}

public sealed class BoundStructLiteralExpression(IReadOnlyList<BoundStructFieldInitializer> fields, TextSpan span, BladeType type) : BoundExpression(BoundNodeKind.StructLiteralExpression, span, type)
{
    public IReadOnlyList<BoundStructFieldInitializer> Fields { get; } = fields;
}

public sealed class BoundConversionExpression(BoundExpression expression, TextSpan span, BladeType targetType) : BoundExpression(BoundNodeKind.ConversionExpression, span, targetType)
{
    public BoundExpression Expression { get; } = expression;
}

public sealed class BoundCastExpression(BoundExpression expression, TextSpan span, BladeType targetType) : BoundExpression(BoundNodeKind.CastExpression, span, targetType)
{
    public BoundExpression Expression { get; } = expression;
}

public sealed class BoundBitcastExpression(BoundExpression expression, TextSpan span, BladeType targetType) : BoundExpression(BoundNodeKind.BitcastExpression, span, targetType)
{
    public BoundExpression Expression { get; } = expression;
}

/// <summary>
/// Only required inside the binder as a sentinel value, not a public api.
/// </summary>
internal sealed class BoundErrorExpression(TextSpan span) : BoundExpression(BoundNodeKind.ErrorExpression, span, BuiltinTypes.Unknown)
{
}

public abstract class BoundAssignmentTarget(BoundNodeKind kind, TextSpan span, BladeType type) : BoundNode(kind, span)
{
    public BladeType Type { get; } = type;
}

public sealed class BoundSymbolAssignmentTarget(Symbol symbol, TextSpan span, BladeType type) : BoundAssignmentTarget(BoundNodeKind.SymbolAssignmentTarget, span, type)
{
    public Symbol Symbol { get; } = symbol;
}

public sealed class BoundMemberAssignmentTarget(BoundExpression receiver, AggregateMemberSymbol member, TextSpan span) : BoundAssignmentTarget(BoundNodeKind.MemberAssignmentTarget, span, Requires.NotNull(member).Type)
{
    public BoundExpression Receiver { get; } = receiver;
    public AggregateMemberSymbol Member { get; } = Requires.NotNull(member);
    public string MemberName => Member.Name;
}

public sealed class BoundBitfieldAssignmentTarget(BoundAssignmentTarget receiverTarget, BoundExpression receiverValue, AggregateMemberSymbol member, TextSpan span) : BoundAssignmentTarget(BoundNodeKind.BitfieldAssignmentTarget, span, Requires.NotNull(member).Type)
{
    public BoundAssignmentTarget ReceiverTarget { get; } = receiverTarget;
    public BoundExpression ReceiverValue { get; } = receiverValue;
    public AggregateMemberSymbol Member { get; } = Requires.NotNull(member);
}

public sealed class BoundIndexAssignmentTarget(BoundExpression expression, BoundExpression index, TextSpan span, BladeType type) : BoundAssignmentTarget(BoundNodeKind.IndexAssignmentTarget, span, type)
{
    public BoundExpression Expression { get; } = expression;
    public BoundExpression Index { get; } = index;
}

public sealed class BoundPointerDerefAssignmentTarget(BoundExpression expression, TextSpan span, BladeType type) : BoundAssignmentTarget(BoundNodeKind.PointerDerefAssignmentTarget, span, type)
{
    public BoundExpression Expression { get; } = expression;
}

public sealed class BoundDiscardAssignmentTarget(TextSpan span, BladeType type) : BoundAssignmentTarget(BoundNodeKind.DiscardAssignmentTarget, span, type)
{
}

/// <summary>
/// Only required inside the binder as a sentinel value, not a public api.
/// </summary>
internal sealed class BoundErrorAssignmentTarget(TextSpan span) : BoundAssignmentTarget(BoundNodeKind.ErrorAssignmentTarget, span, BuiltinTypes.Unknown)
{
}
