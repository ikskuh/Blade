using System.Collections.Generic;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;

namespace Blade.IR.Mir.Optimizations;

[MirOptimization("const-prop", Priority = 800)]
public sealed class MirConstantPropagation : IMirOptimization
{
    public MirModule? Run(MirModule input)
    {
        Requires.NotNull(input);

        List<MirFunction> functions = new(input.Functions.Count);
        foreach (MirFunction function in input.Functions)
        {
            List<MirBlock> blocks = new(function.Blocks.Count);
            foreach (MirBlock block in function.Blocks)
            {
                Dictionary<MirValueId, BladeValue?> constants = [];
                List<MirInstruction> instructions = [];
                foreach (MirInstruction instruction in block.Instructions)
                {
                    MirInstruction rewritten = instruction;
                    if (instruction is MirCopyInstruction copy && TryGetConstant(constants, copy.Source, out BladeValue? copyConstant))
                    {
                        rewritten = new MirConstantInstruction(copy.Result!, copy.ResultType!, copyConstant, copy.Span);
                    }
                    else if (instruction is MirUnaryInstruction unary
                        && TryGetConstant(constants, unary.Operand, out BladeValue? unaryOperand)
                        && TryFoldUnary(unary.Operator, unaryOperand, out object? unaryResult)
                        && TryCreateConstantInstruction(unary.Result!, unary.ResultType!, unaryResult, unary.Span, out MirInstruction foldedUnary))
                    {
                        rewritten = foldedUnary;
                    }
                    else if (instruction is MirBinaryInstruction binary
                        && TryGetConstant(constants, binary.Left, out BladeValue? left)
                        && TryGetConstant(constants, binary.Right, out BladeValue? right)
                        && TryFoldBinary(binary.Operator, left, right, out object? binaryResult)
                        && TryCreateConstantInstruction(binary.Result!, binary.ResultType!, binaryResult, binary.Span, out MirInstruction foldedBinary))
                    {
                        rewritten = foldedBinary;
                    }
                    else if (instruction is MirPointerOffsetInstruction pointerOffset
                        && TryGetConstant(constants, pointerOffset.BaseAddress, out BladeValue? pointerValue)
                        && TryGetConstant(constants, pointerOffset.Delta, out BladeValue? deltaValue)
                        && TryFoldPointerOffset(pointerOffset.OperatorKind, pointerValue, deltaValue, pointerOffset.Stride, out object? offsetResult)
                        && TryCreateConstantInstruction(pointerOffset.Result!, pointerOffset.ResultType!, offsetResult, pointerOffset.Span, out MirInstruction foldedPointerOffset))
                    {
                        rewritten = foldedPointerOffset;
                    }
                    else if (instruction is MirPointerDifferenceInstruction pointerDifference
                        && TryGetConstant(constants, pointerDifference.Left, out BladeValue? leftPointer)
                        && TryGetConstant(constants, pointerDifference.Right, out BladeValue? rightPointer)
                        && TryFoldPointerDifference(leftPointer, rightPointer, pointerDifference.Stride, out object? differenceResult)
                        && TryCreateConstantInstruction(pointerDifference.Result!, pointerDifference.ResultType!, differenceResult, pointerDifference.Span, out MirInstruction foldedPointerDifference))
                    {
                        rewritten = foldedPointerDifference;
                    }
                    else if (instruction is MirConvertInstruction convert
                        && convert.Result is MirValueId convertResult
                        && TryGetConstant(constants, convert.Operand, out BladeValue? convertValue)
                        && convert.ResultType is RuntimeTypeSymbol runtimeType
                        && convertValue is not null)
                    {
                        if (TryCreateConstantInstruction(convertResult, runtimeType, convertValue.Value, convert.Span, out MirInstruction foldedConvert))
                            rewritten = foldedConvert;
                    }

                    if (rewritten.Result is MirValueId result)
                    {
                        if (rewritten is MirConstantInstruction constant)
                        {
                            constants[result] = constant.Value;
                        }
                        else
                        {
                            constants.Remove(result);
                        }
                    }

                    instructions.Add(rewritten);
                }

                MirTerminator terminator = block.Terminator;
                if (terminator is MirBranchTerminator branch
                    && TryGetConstant(constants, branch.Condition, out BladeValue? conditionValue)
                    && TryGetBool(conditionValue, out bool condition))
                {
                    terminator = condition
                        ? new MirGotoTerminator(branch.TrueTarget, branch.TrueArguments, branch.Span)
                        : new MirGotoTerminator(branch.FalseTarget, branch.FalseArguments, branch.Span);
                }

                blocks.Add(new MirBlock(block.Ref, block.Parameters, instructions, terminator));
            }

            functions.Add(new MirFunction(
                function.Symbol,
                function.IsEntryPoint,
                function.ReturnTypes,
                blocks,
                function.ReturnSlots));
        }

        MirModule result2 = new(input.StoragePlaces, input.StorageDefinitions, functions);
        return MirTextWriter.Write(result2) != MirTextWriter.Write(input) ? result2 : null;
    }

    private static bool TryGetConstant(
        IReadOnlyDictionary<MirValueId, BladeValue?> constants,
        MirValueId value,
        out BladeValue? constant)
    {
        return constants.TryGetValue(value, out constant);
    }

    private static bool TryGetBool(BladeValue? value, out bool result)
    {
        switch (value?.Value)
        {
            case bool b:
                result = b;
                return true;
            case sbyte sb:
                result = sb != 0;
                return true;
            case byte by:
                result = by != 0;
                return true;
            case short s:
                result = s != 0;
                return true;
            case ushort us:
                result = us != 0;
                return true;
            case long l:
                result = l != 0;
                return true;
            case int i:
                result = i != 0;
                return true;
            case uint u:
                result = u != 0;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static bool TryCreateConstantInstruction(MirValueId result, TypeSymbol resultType, object? rawValue, TextSpan span, out MirInstruction instruction)
    {
        BladeValue? normalizedValue = null;
        if (rawValue is not null && !ComptimeTypeFacts.TryCreateBladeValue(rawValue, resultType, out normalizedValue))
        {
            instruction = null!;
            return false;
        }

        instruction = new MirConstantInstruction(result, resultType, normalizedValue, span);
        return true;
    }

    private static bool TryFoldUnary(BoundUnaryOperatorKind kind, BladeValue? operand, out object? result)
    {
        result = null;
        switch (kind)
        {
            case BoundUnaryOperatorKind.LogicalNot when TryGetBool(operand, out bool boolValue):
                result = !boolValue;
                return true;

            case BoundUnaryOperatorKind.Negation when TryGetInteger(operand, out long integer):
                result = -integer;
                return true;
            case BoundUnaryOperatorKind.BitwiseNot when TryGetInteger(operand, out long bitwiseInteger):
                result = ~bitwiseInteger;
                return true;
            case BoundUnaryOperatorKind.UnaryPlus when TryGetInteger(operand, out long identityInteger):
                result = identityInteger;
                return true;
        }

        return false;
    }

    private static bool TryFoldBinary(BoundBinaryOperatorKind kind, BladeValue? left, BladeValue? right, out object? result)
    {
        result = null;
        if (TryGetInteger(left, out long leftInt) && TryGetInteger(right, out long rightInt))
        {
            switch (kind)
            {
                case BoundBinaryOperatorKind.Add:
                    result = leftInt + rightInt;
                    return true;
                case BoundBinaryOperatorKind.Subtract:
                    result = leftInt - rightInt;
                    return true;
                case BoundBinaryOperatorKind.Multiply:
                    result = leftInt * rightInt;
                    return true;
                case BoundBinaryOperatorKind.Divide:
                    result = rightInt == 0 ? leftInt : leftInt / rightInt;
                    return true;
                case BoundBinaryOperatorKind.Modulo:
                    result = rightInt == 0 ? leftInt : leftInt % rightInt;
                    return true;
                case BoundBinaryOperatorKind.BitwiseAnd:
                    result = leftInt & rightInt;
                    return true;
                case BoundBinaryOperatorKind.BitwiseOr:
                    result = leftInt | rightInt;
                    return true;
                case BoundBinaryOperatorKind.BitwiseXor:
                    result = leftInt ^ rightInt;
                    return true;
                case BoundBinaryOperatorKind.ShiftLeft:
                    result = leftInt << (int)(rightInt & 63);
                    return true;
                case BoundBinaryOperatorKind.ShiftRight:
                    result = leftInt >> (int)(rightInt & 63);
                    return true;
                case BoundBinaryOperatorKind.ArithmeticShiftLeft:
                    result = leftInt << (int)(rightInt & 63);
                    return true;
                case BoundBinaryOperatorKind.ArithmeticShiftRight:
                    result = leftInt >> (int)(rightInt & 63);
                    return true;
                case BoundBinaryOperatorKind.RotateLeft:
                    result = RotateLeft(leftInt, rightInt);
                    return true;
                case BoundBinaryOperatorKind.RotateRight:
                    result = RotateRight(leftInt, rightInt);
                    return true;
                case BoundBinaryOperatorKind.Equals:
                    result = leftInt == rightInt;
                    return true;
                case BoundBinaryOperatorKind.NotEquals:
                    result = leftInt != rightInt;
                    return true;
                case BoundBinaryOperatorKind.Less:
                    result = leftInt < rightInt;
                    return true;
                case BoundBinaryOperatorKind.LessOrEqual:
                    result = leftInt <= rightInt;
                    return true;
                case BoundBinaryOperatorKind.Greater:
                    result = leftInt > rightInt;
                    return true;
                case BoundBinaryOperatorKind.GreaterOrEqual:
                    result = leftInt >= rightInt;
                    return true;
            }
        }

        if (kind is BoundBinaryOperatorKind.Equals or BoundBinaryOperatorKind.NotEquals)
        {
            bool equals = Equals(left?.Value, right?.Value);
            result = kind == BoundBinaryOperatorKind.Equals ? equals : !equals;
            return true;
        }

        if (TryGetBool(left, out bool leftBool) && TryGetBool(right, out bool rightBool))
        {
            switch (kind)
            {
                case BoundBinaryOperatorKind.LogicalAnd:
                    result = leftBool && rightBool;
                    return true;
                case BoundBinaryOperatorKind.LogicalOr:
                    result = leftBool || rightBool;
                    return true;
            }
        }

        return false;
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

    private static bool TryFoldPointerOffset(BoundBinaryOperatorKind kind, BladeValue? pointerValue, BladeValue? deltaValue, int stride, out object? result)
    {
        result = null;
        if (!TryGetUnsigned32(pointerValue, out uint pointer) || !TryGetUnsigned32(deltaValue, out uint delta))
            return false;

        uint scaledDelta = unchecked(delta * (uint)stride);
        uint offset = kind == BoundBinaryOperatorKind.Add
            ? unchecked(pointer + scaledDelta)
            : unchecked(pointer - scaledDelta);
        result = offset;
        return true;
    }

    private static bool TryFoldPointerDifference(BladeValue? leftValue, BladeValue? rightValue, int stride, out object? result)
    {
        result = null;
        if (!TryGetUnsigned32(leftValue, out uint left) || !TryGetUnsigned32(rightValue, out uint right))
            return false;

        int rawDifference = unchecked((int)(left - right));
        result = rawDifference / stride;
        return true;
    }

    private static bool TryGetInteger(BladeValue? value, out long result)
    {
        switch (value?.Value)
        {
            case sbyte sb:
                result = sb;
                return true;
            case long l:
                result = l;
                return true;
            case int i:
                result = i;
                return true;
            case short s:
                result = s;
                return true;
            case ushort us:
                result = us;
                return true;
            case byte b:
                result = b;
                return true;
            case uint u:
                result = u;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryGetUnsigned32(BladeValue? value, out uint result)
    {
        switch (value?.Value)
        {
            case uint u:
                result = u;
                return true;
            case int i:
                result = unchecked((uint)i);
                return true;
            case long l:
                result = unchecked((uint)l);
                return true;
            case ulong ul:
                result = unchecked((uint)ul);
                return true;
            case short s:
                result = unchecked((uint)s);
                return true;
            case ushort us:
                result = us;
                return true;
            case byte b:
                result = b;
                return true;
            case sbyte sb:
                result = unchecked((uint)sb);
                return true;
            default:
                result = 0;
                return false;
        }
    }

}
