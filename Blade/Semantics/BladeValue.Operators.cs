using System;
using System.Collections.Generic;
using System.Globalization;
using Blade.Semantics.Bound;

namespace Blade.Semantics;

public enum EvaluationError
{
    None,
    TypeMismatch,
    Unsupported,
    UndefinedBehavior,
}

public abstract partial class BladeValue
{
    public static bool AreEqual(BladeValue left, BladeValue right)
    {
        Requires.NotNull(left);
        Requires.NotNull(right);

        if (left.TryGetBool(out bool leftBool) && right.TryGetBool(out bool rightBool))
            return leftBool == rightBool;

        if (left.TryGetInteger(out long leftInteger) && right.TryGetInteger(out long rightInteger))
            return leftInteger == rightInteger;

        if (left.TryGetPointer(out uint leftPointer) && right.TryGetPointer(out uint rightPointer))
            return leftPointer == rightPointer;

        if (left.TryGetString(out string leftString) && right.TryGetString(out string rightString))
            return string.Equals(leftString, rightString, StringComparison.Ordinal);

        if (left.IsVoid && right.IsVoid)
            return true;

        if (left.IsUndefined && right.IsUndefined)
            return true;

        return ReferenceEquals(left.Type, right.Type)
            && Equals(left.Value, right.Value);
    }

    public static EvaluationError TryUnary(BoundUnaryOperatorKind kind, BladeValue value, out BladeValue result)
    {
        Requires.NotNull(value);

        return kind switch
        {
            BoundUnaryOperatorKind.LogicalNot => TryLogicalNot(value, out result),
            BoundUnaryOperatorKind.Negation => TryNegate(value, out result),
            BoundUnaryOperatorKind.BitwiseNot => TryBitwiseNot(value, out result),
            BoundUnaryOperatorKind.UnaryPlus => TryUnaryPlus(value, out result),
            _ => Fail(EvaluationError.Unsupported, out result),
        };
    }

    public static EvaluationError TryLogicalNot(BladeValue value, out BladeValue result)
    {
        Requires.NotNull(value);

        if (!value.TryGetBool(out bool boolean))
            return Fail(EvaluationError.TypeMismatch, out result);

        result = Bool(!boolean);
        return EvaluationError.None;
    }

    public static EvaluationError TryNegate(BladeValue value, out BladeValue result)
    {
        Requires.NotNull(value);

        if (!value.TryGetInteger(out long integer))
            return Fail(EvaluationError.TypeMismatch, out result);

        result = IntegerLiteral(-integer);
        return EvaluationError.None;
    }

    public static EvaluationError TryBitwiseNot(BladeValue value, out BladeValue result)
    {
        Requires.NotNull(value);

        if (!value.TryGetInteger(out long integer))
            return Fail(EvaluationError.TypeMismatch, out result);

        result = IntegerLiteral(~integer);
        return EvaluationError.None;
    }

    public static EvaluationError TryUnaryPlus(BladeValue value, out BladeValue result)
    {
        Requires.NotNull(value);

        if (!value.TryGetInteger(out long integer))
            return Fail(EvaluationError.TypeMismatch, out result);

        result = IntegerLiteral(integer);
        return EvaluationError.None;
    }

    public static EvaluationError TryBinary(BoundBinaryOperatorKind kind, BladeValue left, BladeValue right, out BladeValue result)
    {
        Requires.NotNull(left);
        Requires.NotNull(right);

        if (kind is BoundBinaryOperatorKind.LogicalAnd or BoundBinaryOperatorKind.LogicalOr)
        {
            if (!left.TryGetBool(out bool leftBool) || !right.TryGetBool(out bool rightBool))
                return Fail(EvaluationError.TypeMismatch, out result);

            result = Bool(kind == BoundBinaryOperatorKind.LogicalAnd ? leftBool && rightBool : leftBool || rightBool);
            return EvaluationError.None;
        }

        if (kind is BoundBinaryOperatorKind.Equals or BoundBinaryOperatorKind.NotEquals)
        {
            bool equals = AreEqual(left, right);
            result = Bool(kind == BoundBinaryOperatorKind.Equals ? equals : !equals);
            return EvaluationError.None;
        }

        if (!left.TryGetInteger(out long leftInteger) || !right.TryGetInteger(out long rightInteger))
            return Fail(EvaluationError.TypeMismatch, out result);

        return kind switch
        {
            BoundBinaryOperatorKind.Add => Success(IntegerLiteral(leftInteger + rightInteger), out result),
            BoundBinaryOperatorKind.Subtract => Success(IntegerLiteral(leftInteger - rightInteger), out result),
            BoundBinaryOperatorKind.Multiply => Success(IntegerLiteral(leftInteger * rightInteger), out result),
            BoundBinaryOperatorKind.Divide when rightInteger == 0 => Fail(EvaluationError.UndefinedBehavior, out result),
            BoundBinaryOperatorKind.Divide => Success(IntegerLiteral(leftInteger / rightInteger), out result),
            BoundBinaryOperatorKind.Modulo when rightInteger == 0 => Fail(EvaluationError.UndefinedBehavior, out result),
            BoundBinaryOperatorKind.Modulo => Success(IntegerLiteral(leftInteger % rightInteger), out result),
            BoundBinaryOperatorKind.BitwiseAnd => Success(IntegerLiteral(leftInteger & rightInteger), out result),
            BoundBinaryOperatorKind.BitwiseOr => Success(IntegerLiteral(leftInteger | rightInteger), out result),
            BoundBinaryOperatorKind.BitwiseXor => Success(IntegerLiteral(leftInteger ^ rightInteger), out result),
            BoundBinaryOperatorKind.ShiftLeft => Success(IntegerLiteral(leftInteger << (int)(rightInteger & 63)), out result),
            BoundBinaryOperatorKind.ShiftRight => Success(IntegerLiteral(leftInteger >> (int)(rightInteger & 63)), out result),
            BoundBinaryOperatorKind.ArithmeticShiftLeft => Success(IntegerLiteral(leftInteger << (int)(rightInteger & 63)), out result),
            BoundBinaryOperatorKind.ArithmeticShiftRight => Success(IntegerLiteral(leftInteger >> (int)(rightInteger & 63)), out result),
            BoundBinaryOperatorKind.RotateLeft => Success(IntegerLiteral(RotateLeft(leftInteger, rightInteger)), out result),
            BoundBinaryOperatorKind.RotateRight => Success(IntegerLiteral(RotateRight(leftInteger, rightInteger)), out result),
            BoundBinaryOperatorKind.Less => Success(Bool(leftInteger < rightInteger), out result),
            BoundBinaryOperatorKind.LessOrEqual => Success(Bool(leftInteger <= rightInteger), out result),
            BoundBinaryOperatorKind.Greater => Success(Bool(leftInteger > rightInteger), out result),
            BoundBinaryOperatorKind.GreaterOrEqual => Success(Bool(leftInteger >= rightInteger), out result),
            _ => Fail(EvaluationError.Unsupported, out result),
        };
    }

    public static EvaluationError TryPointerOffset(BoundBinaryOperatorKind kind, BladeValue pointerValue, BladeValue deltaValue, int stride, out BladeValue result)
    {
        Requires.NotNull(pointerValue);
        Requires.NotNull(deltaValue);

        if (!pointerValue.TryGetPointer(out uint pointer) || !TryGetPointerDelta(deltaValue, out uint delta))
            return Fail(EvaluationError.TypeMismatch, out result);

        uint scaledDelta = unchecked(delta * (uint)stride);
        uint address = kind == BoundBinaryOperatorKind.Add
            ? unchecked(pointer + scaledDelta)
            : unchecked(pointer - scaledDelta);

        if (pointerValue.Type is not PointerLikeTypeSymbol pointerType)
            return Fail(EvaluationError.TypeMismatch, out result);

        result = PointerValue(pointerType, address);
        return EvaluationError.None;
    }

    public static EvaluationError TryPointerDifference(BladeValue leftValue, BladeValue rightValue, int stride, out BladeValue result)
    {
        Requires.NotNull(leftValue);
        Requires.NotNull(rightValue);

        if (!leftValue.TryGetPointer(out uint leftPointer) || !rightValue.TryGetPointer(out uint rightPointer))
            return Fail(EvaluationError.TypeMismatch, out result);

        int rawDifference = unchecked((int)(leftPointer - rightPointer));
        result = IntegerLiteral(rawDifference / stride);
        return EvaluationError.None;
    }

    public static EvaluationError TryConvert(BladeValue value, TypeSymbol type, out BladeValue result)
    {
        Requires.NotNull(value);
        Requires.NotNull(type);

        if (type is VoidTypeSymbol)
        {
            result = Void;
            return EvaluationError.None;
        }

        if (value.IsUndefined)
        {
            result = Undefined;
            return EvaluationError.None;
        }

        if (type is UnknownTypeSymbol || ReferenceEquals(value.Type, type))
        {
            result = value;
            return EvaluationError.None;
        }

        if (type is UndefinedLiteralTypeSymbol)
            return Fail(EvaluationError.TypeMismatch, out result);

        if (ReferenceEquals(type, BuiltinTypes.Bool))
        {
            if (value.TryGetBool(out bool boolValue))
            {
                result = Bool(boolValue);
                return EvaluationError.None;
            }

            if (!TryGetIntegerLikeValue(value, out long integerBool))
                return Fail(EvaluationError.TypeMismatch, out result);

            result = Bool(integerBool != 0);
            return EvaluationError.None;
        }

        if (ReferenceEquals(type, BuiltinTypes.IntegerLiteral))
        {
            if (!TryGetIntegerLikeValue(value, out long integerLiteral))
                return Fail(EvaluationError.TypeMismatch, out result);

            result = IntegerLiteral(integerLiteral);
            return EvaluationError.None;
        }

        if (type is EnumTypeSymbol enumType)
            return TryConvertToRuntimeType(value, enumType, out result);

        if (type is BitfieldTypeSymbol bitfieldType)
            return TryConvertToRuntimeType(value, bitfieldType, out result);

        if (type is RuntimeTypeSymbol runtimeType)
            return TryConvertToRuntimeType(value, runtimeType, out result);

        if (type.IsLegalRuntimeObject(value.Value))
        {
            result = CreateTypedValue(type, value.Value);
            return EvaluationError.None;
        }

        return Fail(EvaluationError.TypeMismatch, out result);
    }

    public static EvaluationError TryCast(BladeValue value, TypeSymbol type, out BladeValue result)
    {
        return TryConvert(value, type, out result);
    }

    public static EvaluationError TryBitCast(BladeValue value, RuntimeTypeSymbol type, out BladeValue result)
    {
        Requires.NotNull(value);
        Requires.NotNull(type);

        if (type is not { IsScalarCastType: true, ScalarWidthBits: int targetWidth })
            return Fail(EvaluationError.Unsupported, out result);

        if (!TryGetBitCastSourceBits(value, targetWidth, out int sourceWidth, out uint rawBits))
            return Fail(EvaluationError.Unsupported, out result);

        if (sourceWidth != targetWidth)
            return Fail(EvaluationError.TypeMismatch, out result);

        return TryCreateScalarValueFromBits(type, rawBits, out result);
    }

    private static EvaluationError TryConvertToRuntimeType(BladeValue value, RuntimeTypeSymbol type, out BladeValue result)
    {
        if (type.IsLegalRuntimeObject(value.Value))
        {
            result = new RuntimeBladeValue(type, value.Value);
            return EvaluationError.None;
        }

        if (type is IntegerTypeSymbol integerType)
        {
            if (!TryConvertToUInt32Bits(value, out uint integerBits))
                return Fail(EvaluationError.TypeMismatch, out result);

            result = new RuntimeBladeValue(integerType, NormalizeIntegerBits(integerType, integerBits));
            return EvaluationError.None;
        }

        if (type is PointerLikeTypeSymbol pointerType)
        {
            if (!TryConvertToUInt32Bits(value, out uint pointerBits))
                return Fail(EvaluationError.TypeMismatch, out result);

            result = PointerValue(pointerType, pointerBits);
            return EvaluationError.None;
        }

        if (type is EnumTypeSymbol enumType)
        {
            if (TryConvertToRuntimeType(value, enumType.BackingType, out BladeValue backingValue) != EvaluationError.None)
                return Fail(EvaluationError.TypeMismatch, out result);

            result = new RuntimeBladeValue(enumType, backingValue.Value);
            return EvaluationError.None;
        }

        if (type is BitfieldTypeSymbol bitfieldType)
        {
            if (TryConvertToRuntimeType(value, bitfieldType.BackingType, out BladeValue backingValue) != EvaluationError.None)
                return Fail(EvaluationError.TypeMismatch, out result);

            result = new RuntimeBladeValue(bitfieldType, backingValue.Value);
            return EvaluationError.None;
        }

        return Fail(EvaluationError.TypeMismatch, out result);
    }

    private static bool TryGetIntegerLikeValue(BladeValue value, out long result)
    {
        Requires.NotNull(value);

        if (value.TryGetInteger(out result))
            return true;

        if (value.TryGetPointer(out uint pointerValue))
        {
            result = pointerValue;
            return true;
        }

        result = 0L;
        return false;
    }

    private static bool TryConvertToUInt32Bits(BladeValue value, out uint result)
    {
        Requires.NotNull(value);

        if (value.TryGetPointer(out result))
            return true;

        if (value.TryGetInteger(out long integerValue))
        {
            result = unchecked((uint)integerValue);
            return true;
        }

        result = 0U;
        return false;
    }

    private static bool TryGetPointerDelta(BladeValue value, out uint delta)
    {
        Requires.NotNull(value);

        if (!value.TryGetInteger(out long integerValue))
        {
            delta = 0U;
            return false;
        }

        delta = unchecked((uint)integerValue);
        return true;
    }

    private static bool TryGetBitCastSourceBits(BladeValue value, int targetWidth, out int sourceWidth, out uint rawBits)
    {
        Requires.NotNull(value);

        if (value.Type is RuntimeTypeSymbol { IsScalarCastType: true, ScalarWidthBits: int runtimeWidth })
        {
            sourceWidth = runtimeWidth;
            if (value.TryGetInteger(out long integerValue))
            {
                rawBits = unchecked((uint)integerValue);
                return true;
            }

            if (value.TryGetPointer(out uint pointerValue))
            {
                rawBits = pointerValue;
                return true;
            }

            rawBits = 0U;
            return false;
        }

        if (ReferenceEquals(value.Type, BuiltinTypes.IntegerLiteral) && value.TryGetInteger(out long literalValue))
        {
            sourceWidth = targetWidth;
            rawBits = unchecked((uint)literalValue);
            return true;
        }

        sourceWidth = 0;
        rawBits = 0U;
        return false;
    }

    private static EvaluationError TryCreateScalarValueFromBits(RuntimeTypeSymbol type, uint rawBits, out BladeValue result)
    {
        if (type is IntegerTypeSymbol integerType)
        {
            result = new RuntimeBladeValue(integerType, NormalizeIntegerBits(integerType, rawBits));
            return EvaluationError.None;
        }

        if (type is PointerLikeTypeSymbol pointerType)
        {
            result = PointerValue(pointerType, rawBits);
            return EvaluationError.None;
        }

        if (type is EnumTypeSymbol enumType)
        {
            EvaluationError error = TryCreateScalarValueFromBits(enumType.BackingType, rawBits, out BladeValue backingValue);
            if (error != EvaluationError.None)
                return Fail(error, out result);

            result = new RuntimeBladeValue(enumType, backingValue.Value);
            return EvaluationError.None;
        }

        if (type is BitfieldTypeSymbol bitfieldType)
        {
            EvaluationError error = TryCreateScalarValueFromBits(bitfieldType.BackingType, rawBits, out BladeValue backingValue);
            if (error != EvaluationError.None)
                return Fail(error, out result);

            result = new RuntimeBladeValue(bitfieldType, backingValue.Value);
            return EvaluationError.None;
        }

        return Fail(EvaluationError.Unsupported, out result);
    }

    private static long NormalizeIntegerBits(IntegerTypeSymbol type, uint rawBits)
    {
        Requires.NotNull(type);

        uint maskedBits = type.BitWidth >= 32
            ? rawBits
            : rawBits & ((1u << type.BitWidth) - 1u);

        if (!type.IsSignedInteger)
            return maskedBits;

        if (type.BitWidth >= 32)
            return unchecked((int)maskedBits);

        int signedValue = (int)(maskedBits << (32 - type.BitWidth)) >> (32 - type.BitWidth);
        return signedValue;
    }

    private static BladeValue CreateTypedValue(TypeSymbol type, object value)
    {
        return type switch
        {
            RuntimeTypeSymbol runtimeType => new RuntimeBladeValue(runtimeType, value),
            ComptimeTypeSymbol comptimeType => new ComptimeBladeValue(comptimeType, value),
            _ => Assert.UnreachableValue<BladeValue>(),
        };
    }

    private static long RotateLeft(long value, long shift)
    {
        int amount = (int)(shift & 31);
        uint bits = unchecked((uint)value);
        return unchecked((int)((bits << amount) | (bits >> ((32 - amount) & 31))));
    }

    private static long RotateRight(long value, long shift)
    {
        int amount = (int)(shift & 31);
        uint bits = unchecked((uint)value);
        return unchecked((int)((bits >> amount) | (bits << ((32 - amount) & 31))));
    }

    private static EvaluationError Success(BladeValue value, out BladeValue result)
    {
        result = Requires.NotNull(value);
        return EvaluationError.None;
    }

    private static EvaluationError Fail(EvaluationError error, out BladeValue result)
    {
        result = Undefined;
        return error;
    }
}
