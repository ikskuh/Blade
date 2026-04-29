using System;
using System.Collections.Generic;
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
    private static bool EqualsCore(BladeValue left, BladeValue? right)
    {
        Requires.NotNull(left);

        if (right is null)
            return false;

        if (left.TryGetBool(out bool leftBool) && right.TryGetBool(out bool rightBool))
            return leftBool == rightBool;

        if (left.TryGetInteger(out long leftInteger) && right.TryGetInteger(out long rightInteger))
            return leftInteger == rightInteger;

        if (left.TryGetPointedValue(out PointedValue leftPointed) && right.TryGetPointedValue(out PointedValue rightPointed))
            return AreEqualPointers(left, leftPointed, right, rightPointed);

        if (TryGetRuntimeArray(left, out IReadOnlyList<RuntimeBladeValue> leftElements)
            && TryGetRuntimeArray(right, out IReadOnlyList<RuntimeBladeValue> rightElements))
        {
            return AreEqualArrays(leftElements, rightElements);
        }

        if (left.Value is IReadOnlyDictionary<string, BladeValue> leftFields
            && right.Value is IReadOnlyDictionary<string, BladeValue> rightFields)
        {
            return left.Type == right.Type
                && AreEqualAggregates(leftFields, rightFields);
        }

        if (left.IsVoid && right.IsVoid)
            return true;

        if (left.IsUndefined && right.IsUndefined)
            return true;

        return left.Type == right.Type
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
            bool equals = left == right;
            result = Bool(kind == BoundBinaryOperatorKind.Equals ? equals : !equals);
            return EvaluationError.None;
        }

        EvaluationError pointerError = TryPointerBinary(kind, left, right, out result);
        if (pointerError != EvaluationError.TypeMismatch)
            return pointerError;

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

        if (pointerValue.Type is not PointerLikeTypeSymbol pointerType)
            return Fail(EvaluationError.TypeMismatch, out result);

        if (!pointerValue.TryGetPointedValue(out PointedValue pointedValue) || !deltaValue.TryGetInteger(out long delta))
            return Fail(EvaluationError.TypeMismatch, out result);

        long signedStride = checked(delta * stride);
        long signedOffset = kind == BoundBinaryOperatorKind.Add
            ? pointedValue.Offset + signedStride
            : pointedValue.Offset - signedStride;
        if (signedOffset < int.MinValue || signedOffset > int.MaxValue)
            return Fail(EvaluationError.UndefinedBehavior, out result);

        result = Pointer(pointerType, pointedValue.WithOffset((int)signedOffset));
        return EvaluationError.None;
    }

    public static EvaluationError TryPointerDifference(BladeValue leftValue, BladeValue rightValue, int stride, out BladeValue result)
    {
        Requires.NotNull(leftValue);
        Requires.NotNull(rightValue);

        if (!leftValue.TryGetPointedValue(out PointedValue leftPointed) || !rightValue.TryGetPointedValue(out PointedValue rightPointed))
            return Fail(EvaluationError.TypeMismatch, out result);

        if (!ReferenceEquals(leftPointed.Symbol, rightPointed.Symbol))
            return Fail(EvaluationError.Unsupported, out result);

        int rawDifference = leftPointed.Offset - rightPointed.Offset;
        if (rawDifference % stride != 0)
            return Fail(EvaluationError.UndefinedBehavior, out result);

        result = IntegerLiteral(rawDifference / stride);
        return EvaluationError.None;
    }

    public static EvaluationError TryConvert(BladeValue value, BladeType type, out BladeValue result)
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

        if (type is UnknownTypeSymbol || value.Type == type)
        {
            result = value;
            return EvaluationError.None;
        }

        if (type is UndefinedLiteralTypeSymbol)
            return Fail(EvaluationError.TypeMismatch, out result);

        if (type == BuiltinTypes.Bool)
        {
            if (!value.TryGetBool(out bool boolValue))
                return Fail(EvaluationError.TypeMismatch, out result);

            result = Bool(boolValue);
            return EvaluationError.None;
        }

        if (type == BuiltinTypes.IntegerLiteral)
        {
            if (!value.TryGetInteger(out long integerLiteral))
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

    public static EvaluationError TryCast(BladeValue value, BladeType type, out BladeValue result)
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

    private static EvaluationError TryPointerBinary(BoundBinaryOperatorKind kind, BladeValue left, BladeValue right, out BladeValue result)
    {
        if (left.Type is not PointerLikeTypeSymbol leftPointerType)
            return Fail(EvaluationError.TypeMismatch, out result);

        if (kind is BoundBinaryOperatorKind.Add or BoundBinaryOperatorKind.Subtract
            && right.TryGetInteger(out _)
            && leftPointerType is MultiPointerTypeSymbol)
        {
            int stride = leftPointerType.PointeeType.GetPointerElementStride(leftPointerType.StorageClass);
            return TryPointerOffset(kind, left, right, stride, out result);
        }

        if (kind == BoundBinaryOperatorKind.Subtract
            && right.Type is MultiPointerTypeSymbol rightPointerType
            && leftPointerType is MultiPointerTypeSymbol
            && right.TryGetPointedValue(out _))
        {
            int stride = leftPointerType.PointeeType.GetPointerElementStride(leftPointerType.StorageClass);
            return TryPointerDifference(left, right, stride, out result);
        }

        if (kind is BoundBinaryOperatorKind.Less or BoundBinaryOperatorKind.LessOrEqual or BoundBinaryOperatorKind.Greater or BoundBinaryOperatorKind.GreaterOrEqual)
            return TryPointerComparison(kind, left, right, out result);

        return Fail(EvaluationError.TypeMismatch, out result);
    }

    private static EvaluationError TryPointerComparison(BoundBinaryOperatorKind kind, BladeValue left, BladeValue right, out BladeValue result)
    {
        if (left.Type is not PointerLikeTypeSymbol leftPointerType
            || right.Type is not PointerLikeTypeSymbol rightPointerType
            || !left.TryGetPointedValue(out PointedValue leftPointed)
            || !right.TryGetPointedValue(out PointedValue rightPointed))
        {
            return Fail(EvaluationError.TypeMismatch, out result);
        }

        int comparison;
        if (ReferenceEquals(leftPointed.Symbol, rightPointed.Symbol))
        {
            comparison = leftPointed.Offset.CompareTo(rightPointed.Offset);
        }
        else
        {
            if (leftPointerType.StorageClass != rightPointerType.StorageClass
                || !TryGetKnownAbsoluteAddress(leftPointerType, leftPointed, out int leftAddress)
                || !TryGetKnownAbsoluteAddress(rightPointerType, rightPointed, out int rightAddress))
            {
                return Fail(EvaluationError.Unsupported, out result);
            }

            comparison = leftAddress.CompareTo(rightAddress);
        }

        bool value = kind switch
        {
            BoundBinaryOperatorKind.Less => comparison < 0,
            BoundBinaryOperatorKind.LessOrEqual => comparison <= 0,
            BoundBinaryOperatorKind.Greater => comparison > 0,
            BoundBinaryOperatorKind.GreaterOrEqual => comparison >= 0,
            _ => Assert.UnreachableValue<bool>(), // pragma: force-coverage
        };

        result = Bool(value);
        return EvaluationError.None;
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
            if (!value.TryGetInteger(out long integerValue))
                return Fail(EvaluationError.TypeMismatch, out result);

            result = new RuntimeBladeValue(integerType, NormalizeIntegerBits(integerType, unchecked((uint)integerValue)));
            return EvaluationError.None;
        }

        if (type is PointerLikeTypeSymbol pointerType)
        {
            if (value.TryGetPointedValue(out PointedValue pointedValue))
            {
                result = Pointer(pointerType, pointedValue);
                return EvaluationError.None;
            }

            if (value.TryGetInteger(out long absoluteAddress))
            {
                AbsoluteAddressSymbol absoluteSymbol = new(new VirtualAddress(pointerType.StorageClass, unchecked((int)(uint)absoluteAddress)));
                result = Pointer(pointerType, new PointedValue(absoluteSymbol, 0));
                return EvaluationError.None;
            }

            return Fail(EvaluationError.TypeMismatch, out result);
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

    private static bool TryGetBitCastSourceBits(BladeValue value, int targetWidth, out int sourceWidth, out uint rawBits)
    {
        Requires.NotNull(value);

        if (value.Type is RuntimeTypeSymbol { IsScalarCastType: true, ScalarWidthBits: int runtimeWidth })
        {
            sourceWidth = runtimeWidth;
            if (value.TryGetBool(out bool boolValue))
            {
                rawBits = boolValue ? 1u : 0u;
                return true;
            }

            if (value.TryGetInteger(out long integerValue))
            {
                rawBits = unchecked((uint)integerValue);
                return true;
            }

            if (value.Type is PointerLikeTypeSymbol pointerType
                && value.TryGetPointedValue(out PointedValue pointedValue)
                && TryGetKnownAbsoluteAddress(pointerType, pointedValue, out int absoluteAddress))
            {
                rawBits = unchecked((uint)absoluteAddress);
                return true;
            }

            rawBits = 0U;
            return false;
        }

        if (value.Type == BuiltinTypes.IntegerLiteral && value.TryGetInteger(out long literalValue))
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
        if (type is BoolTypeSymbol)
        {
            result = Bool((rawBits & 1u) != 0);
            return EvaluationError.None;
        }

        if (type is IntegerTypeSymbol integerType)
        {
            result = new RuntimeBladeValue(integerType, NormalizeIntegerBits(integerType, rawBits));
            return EvaluationError.None;
        }

        if (type is PointerLikeTypeSymbol pointerType)
        {
            AbsoluteAddressSymbol absoluteSymbol = new(new VirtualAddress(pointerType.StorageClass, unchecked((int)rawBits)));
            result = Pointer(pointerType, new PointedValue(absoluteSymbol, 0));
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

    private static bool AreEqualPointers(BladeValue left, PointedValue leftPointed, BladeValue right, PointedValue rightPointed)
    {
        if (left.Type is not PointerLikeTypeSymbol leftPointerType
            || right.Type is not PointerLikeTypeSymbol rightPointerType
            || leftPointerType.StorageClass != rightPointerType.StorageClass)
        {
            return false;
        }

        if (ReferenceEquals(leftPointed.Symbol, rightPointed.Symbol))
            return leftPointed.Offset == rightPointed.Offset;

        if (!TryGetAbsolutePointerIdentity(left, leftPointed, out VirtualAddress leftAddress)
            || !TryGetAbsolutePointerIdentity(right, rightPointed, out VirtualAddress rightAddress))
        {
            return false;
        }

        return leftAddress == rightAddress;
    }

    private static bool TryGetRuntimeArray(BladeValue value, out IReadOnlyList<RuntimeBladeValue> elements)
    {
        if (value.Type is ArrayTypeSymbol && value.Value is IReadOnlyList<RuntimeBladeValue> runtimeElements)
        {
            elements = runtimeElements;
            return true;
        }

        elements = [];
        return false;
    }

    private static bool AreEqualArrays(IReadOnlyList<RuntimeBladeValue> left, IReadOnlyList<RuntimeBladeValue> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
                return false;
        }

        return true;
    }

    private static bool AreEqualAggregates(
        IReadOnlyDictionary<string, BladeValue> left,
        IReadOnlyDictionary<string, BladeValue> right)
    {
        if (left.Count != right.Count)
            return false;

        foreach ((string fieldName, BladeValue leftValue) in left)
        {
            if (!right.TryGetValue(fieldName, out BladeValue? rightValue) || leftValue != rightValue)
                return false;
        }

        return true;
    }

    private static bool TryGetAbsolutePointerIdentity(BladeValue value, PointedValue pointedValue, out VirtualAddress address)
    {
        Requires.NotNull(value);
        Requires.NotNull(pointedValue);

        PointerLikeTypeSymbol pointerType = (PointerLikeTypeSymbol)value.Type;

        if (pointedValue.Symbol is AbsoluteAddressSymbol absoluteSymbol)
        {
            if (absoluteSymbol.StorageClass != pointerType.StorageClass)
            {
                address = default;
                return false;
            }

            address = AddressMath.AddAddressUnits(absoluteSymbol.Address, pointedValue.Offset);
            return true;
        }

        if (pointedValue.Symbol is GlobalVariableSymbol { FixedAddress: VirtualAddress fixedAddress, StorageClass: var fixedStorageClass })
        {
            if (fixedStorageClass != pointerType.StorageClass)
            {
                address = default;
                return false;
            }

            address = AddressMath.AddAddressUnits(fixedAddress, pointedValue.Offset);
            return true;
        }

        address = default;
        return false;
    }

    private static bool TryGetKnownAbsoluteAddress(PointerLikeTypeSymbol type, PointedValue value, out int address)
    {
        RuntimeBladeValue wrapper = Pointer(type, value);
        if (TryGetAbsolutePointerIdentity(wrapper, value, out VirtualAddress absoluteAddress))
        {
            (_, address) = absoluteAddress.GetDataAddress();
            return true;
        }

        address = 0;
        return false;
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

    private static BladeValue CreateTypedValue(BladeType type, object value)
    {
        return type switch
        {
            RuntimeTypeSymbol runtimeType => new RuntimeBladeValue(runtimeType, value),
            ComptimeTypeSymbol comptimeType => new ComptimeBladeValue(comptimeType, value),
            _ => Assert.UnreachableValue<BladeValue>(), // pragma: force-coverage
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
