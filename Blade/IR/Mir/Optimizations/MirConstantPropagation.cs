using System.Collections.Generic;
using Blade.Semantics;
using Blade.Semantics.Bound;

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
                Dictionary<MirValueId, object?> constants = [];
                List<MirInstruction> instructions = [];
                foreach (MirInstruction instruction in block.Instructions)
                {
                    MirInstruction rewritten = instruction;
                    if (instruction is MirCopyInstruction copy && TryGetConstant(constants, copy.Source, out object? copyConstant))
                    {
                        rewritten = new MirConstantInstruction(copy.Result!.Value, copy.ResultType!, copyConstant, copy.Span);
                    }
                    else if (instruction is MirUnaryInstruction unary
                        && TryGetConstant(constants, unary.Operand, out object? unaryOperand)
                        && TryFoldUnary(unary.Operator, unaryOperand, out object? unaryResult))
                    {
                        rewritten = new MirConstantInstruction(unary.Result!.Value, unary.ResultType!, unaryResult, unary.Span);
                    }
                    else if (instruction is MirBinaryInstruction binary
                        && TryGetConstant(constants, binary.Left, out object? left)
                        && TryGetConstant(constants, binary.Right, out object? right)
                        && TryFoldBinary(binary.Operator, left, right, out object? binaryResult))
                    {
                        rewritten = new MirConstantInstruction(binary.Result!.Value, binary.ResultType!, binaryResult, binary.Span);
                    }
                    else if (instruction is MirSelectInstruction select
                        && TryGetConstant(constants, select.Condition, out object? selectCondition)
                        && TryGetBool(selectCondition, out bool selectValue))
                    {
                        MirValueId source = selectValue ? select.WhenTrue : select.WhenFalse;
                        rewritten = new MirCopyInstruction(select.Result!.Value, select.ResultType!, source, select.Span);
                    }
                    else if (instruction is MirConvertInstruction convert
                        && convert.Result is MirValueId convertResult
                        && TryGetConstant(constants, convert.Operand, out object? convertValue)
                        && TypeFacts.TryNormalizeValue(convertValue, convert.ResultType!, out object? normalizedValue))
                    {
                        rewritten = new MirConstantInstruction(convertResult, convert.ResultType!, normalizedValue, convert.Span);
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
                    && TryGetConstant(constants, branch.Condition, out object? conditionValue)
                    && TryGetBool(conditionValue, out bool condition))
                {
                    terminator = condition
                        ? new MirGotoTerminator(branch.TrueLabel, branch.TrueArguments, branch.Span)
                        : new MirGotoTerminator(branch.FalseLabel, branch.FalseArguments, branch.Span);
                }

                blocks.Add(new MirBlock(block.Label, block.Parameters, instructions, terminator));
            }

            functions.Add(new MirFunction(
                function.Name,
                function.IsEntryPoint,
                function.Kind,
                function.ReturnTypes,
                blocks,
                function.ReturnSlots));
        }

        MirModule result2 = new(input.StoragePlaces, functions);
        return MirTextWriter.Write(result2) != MirTextWriter.Write(input) ? result2 : null;
    }

    private static bool TryGetConstant(
        IReadOnlyDictionary<MirValueId, object?> constants,
        MirValueId value,
        out object? constant)
    {
        return constants.TryGetValue(value, out constant);
    }

    private static bool TryGetBool(object? value, out bool result)
    {
        switch (value)
        {
            case bool b:
                result = b;
                return true;
            case long l:
                result = l != 0;
                return true;
            case int i:
                result = i != 0;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static bool TryFoldUnary(BoundUnaryOperatorKind kind, object? operand, out object? result)
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

    private static bool TryFoldBinary(BoundBinaryOperatorKind kind, object? left, object? right, out object? result)
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
            bool equals = Equals(left, right);
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

    private static bool TryGetInteger(object? value, out long result)
    {
        switch (value)
        {
            case long l:
                result = l;
                return true;
            case int i:
                result = i;
                return true;
            case short s:
                result = s;
                return true;
            case byte b:
                result = b;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}
