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
                    if (instruction is MirCopyInstruction copy
                        && TryGetConstant(constants, copy.Source, out BladeValue? copyConstant))
                    {
                        rewritten = new MirConstantInstruction(copy.Result!, copy.ResultType!, copyConstant, copy.Span);
                    }
                    else if (instruction is MirUnaryInstruction unary
                        && TryGetConstant(constants, unary.Operand, out BladeValue? unaryOperand)
                        && unaryOperand is not null
                        && BladeValue.TryUnary(unary.Operator, unaryOperand, out BladeValue unaryResult) == EvaluationError.None
                        && TryCreateConstantInstruction(unary.Result!, unary.ResultType!, unaryResult, unary.Span, out MirInstruction foldedUnary))
                    {
                        rewritten = foldedUnary;
                    }
                    else if (instruction is MirBinaryInstruction binary
                        && TryGetConstant(constants, binary.Left, out BladeValue? left)
                        && TryGetConstant(constants, binary.Right, out BladeValue? right)
                        && left is not null
                        && right is not null
                        && BladeValue.TryBinary(binary.Operator, left, right, out BladeValue binaryResult) == EvaluationError.None
                        && TryCreateConstantInstruction(binary.Result!, binary.ResultType!, binaryResult, binary.Span, out MirInstruction foldedBinary))
                    {
                        rewritten = foldedBinary;
                    }
                    else if (instruction is MirPointerOffsetInstruction pointerOffset
                        && TryGetConstant(constants, pointerOffset.BaseAddress, out BladeValue? pointerValue)
                        && TryGetConstant(constants, pointerOffset.Delta, out BladeValue? deltaValue)
                        && pointerValue is not null
                        && deltaValue is not null
                        && BladeValue.TryPointerOffset(pointerOffset.OperatorKind, pointerValue, deltaValue, pointerOffset.Stride, out BladeValue offsetResult) == EvaluationError.None
                        && TryCreateConstantInstruction(pointerOffset.Result!, pointerOffset.ResultType!, offsetResult, pointerOffset.Span, out MirInstruction foldedPointerOffset))
                    {
                        rewritten = foldedPointerOffset;
                    }
                    else if (instruction is MirPointerDifferenceInstruction pointerDifference
                        && TryGetConstant(constants, pointerDifference.Left, out BladeValue? leftPointer)
                        && TryGetConstant(constants, pointerDifference.Right, out BladeValue? rightPointer)
                        && leftPointer is not null
                        && rightPointer is not null
                        && BladeValue.TryPointerDifference(leftPointer, rightPointer, pointerDifference.Stride, out BladeValue differenceResult) == EvaluationError.None
                        && TryCreateConstantInstruction(pointerDifference.Result!, pointerDifference.ResultType!, differenceResult, pointerDifference.Span, out MirInstruction foldedPointerDifference))
                    {
                        rewritten = foldedPointerDifference;
                    }
                    else if (instruction is MirConvertInstruction convert
                        && convert.Result is MirValueId convertResult
                        && TryGetConstant(constants, convert.Operand, out BladeValue? convertValue)
                        && convertValue is not null
                        && BladeValue.TryConvert(convertValue, convert.ResultType!, out BladeValue convertedValue) == EvaluationError.None)
                    {
                        rewritten = new MirConstantInstruction(convertResult, convert.ResultType!, convertedValue, convert.Span);
                    }

                    if (rewritten.Result is MirValueId valueId)
                    {
                        if (rewritten is MirConstantInstruction constant)
                            constants[valueId] = constant.Value;
                        else
                            constants.Remove(valueId);
                    }

                    instructions.Add(rewritten);
                }

                MirTerminator terminator = block.Terminator;
                if (terminator is MirBranchTerminator branch
                    && TryGetConstant(constants, branch.Condition, out BladeValue? conditionValue)
                    && conditionValue is not null
                    && conditionValue.TryGetBool(out bool condition))
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
                function.ReturnSlots,
                function.FlagValues));
        }

        MirModule result = new(input.Image, input.StoragePlaces, input.StorageDefinitions, functions);
        return MirTextWriter.Write(result) != MirTextWriter.Write(input) ? result : null;
    }

    private static bool TryGetConstant(IReadOnlyDictionary<MirValueId, BladeValue?> constants, MirValueId value, out BladeValue? constant)
    {
        return constants.TryGetValue(value, out constant);
    }

    private static bool TryCreateConstantInstruction(MirValueId result, BladeType resultType, BladeValue? rawValue, TextSpan span, out MirInstruction instruction)
    {
        if (rawValue is null)
        {
            instruction = new MirConstantInstruction(result, resultType, null, span);
            return true;
        }

        if (BladeValue.TryConvert(rawValue, resultType, out BladeValue normalizedValue) != EvaluationError.None)
        {
            instruction = null!;
            return false;
        }

        instruction = new MirConstantInstruction(result, resultType, normalizedValue, span);
        return true;
    }
}
