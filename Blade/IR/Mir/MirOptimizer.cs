using System;
using System.Collections.Generic;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;

namespace Blade.IR.Mir;

public static class MirOptimizer
{
    public static MirModule Optimize(MirModule module, int maxIterations, bool enableCostBasedInlining)
    {
        MirModule current = module;
        int iterations = Math.Max(1, maxIterations);
        for (int i = 0; i < iterations; i++)
        {
            string before = MirTextWriter.Write(current);
            if (enableCostBasedInlining)
                current = MirInliner.InlineCostBased(current, inlineCostThreshold: 12);
            current = RunConstantPropagation(current);
            current = RunCopyPropagation(current);
            current = RunDeadCodeElimination(current);
            string after = MirTextWriter.Write(current);
            if (before == after)
                break;
        }

        return current;
    }

    private static MirModule RunConstantPropagation(MirModule module)
    {
        List<MirFunction> functions = new(module.Functions.Count);
        foreach (MirFunction function in module.Functions)
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
                    else if (instruction is MirOpInstruction op
                        && op.Opcode == "convert"
                        && op.Result is MirValueId convertResult
                        && op.Operands.Count == 1
                        && TryGetConstant(constants, op.Operands[0], out object? convertValue))
                    {
                        rewritten = new MirConstantInstruction(convertResult, op.ResultType!, convertValue, op.Span);
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
                blocks));
        }

        return new MirModule(functions);
    }

    private static MirModule RunCopyPropagation(MirModule module)
    {
        List<MirFunction> functions = new(module.Functions.Count);
        foreach (MirFunction function in module.Functions)
        {
            List<MirBlock> blocks = new(function.Blocks.Count);
            foreach (MirBlock block in function.Blocks)
            {
                Dictionary<MirValueId, MirValueId> aliases = [];
                List<MirInstruction> instructions = [];
                foreach (MirInstruction instruction in block.Instructions)
                {
                    Dictionary<MirValueId, MirValueId> mapping = ResolveAliasMap(aliases);
                    MirInstruction rewritten = instruction.RewriteUses(mapping);
                    instructions.Add(rewritten);

                    if (rewritten.Result is MirValueId result)
                        aliases.Remove(result);

                    if (rewritten is MirCopyInstruction copy && rewritten.Result is MirValueId copyResult)
                        aliases[copyResult] = ResolveAlias(copy.Source, aliases);
                }

                MirTerminator terminator = block.Terminator.RewriteUses(ResolveAliasMap(aliases));
                blocks.Add(new MirBlock(block.Label, block.Parameters, instructions, terminator));
            }

            functions.Add(new MirFunction(
                function.Name,
                function.IsEntryPoint,
                function.Kind,
                function.ReturnTypes,
                blocks));
        }

        return new MirModule(functions);
    }

    private static MirModule RunDeadCodeElimination(MirModule module)
    {
        List<MirFunction> functions = new(module.Functions.Count);
        foreach (MirFunction function in module.Functions)
        {
            HashSet<string> reachable = ComputeReachableBlocks(function);
            List<MirBlock> blocks = [];
            foreach (MirBlock block in function.Blocks)
            {
                if (!reachable.Contains(block.Label))
                    continue;

                HashSet<MirValueId> live = [];
                foreach (MirValueId used in block.Terminator.Uses)
                    live.Add(used);

                List<MirInstruction> kept = [];
                for (int i = block.Instructions.Count - 1; i >= 0; i--)
                {
                    MirInstruction instruction = block.Instructions[i];
                    bool keep = instruction.HasSideEffects
                        || instruction.Result is null
                        || live.Contains(instruction.Result.Value);

                    if (!keep)
                        continue;

                    kept.Add(instruction);
                    foreach (MirValueId used in instruction.Uses)
                        live.Add(used);
                }

                kept.Reverse();
                blocks.Add(new MirBlock(block.Label, block.Parameters, kept, block.Terminator));
            }

            functions.Add(new MirFunction(
                function.Name,
                function.IsEntryPoint,
                function.Kind,
                function.ReturnTypes,
                blocks));
        }

        return new MirModule(functions);
    }

    private static HashSet<string> ComputeReachableBlocks(MirFunction function)
    {
        HashSet<string> reachable = [];
        if (function.Blocks.Count == 0)
            return reachable;

        Dictionary<string, MirBlock> byLabel = [];
        foreach (MirBlock block in function.Blocks)
            byLabel[block.Label] = block;

        Queue<string> pending = new();
        pending.Enqueue(function.Blocks[0].Label);
        while (pending.Count > 0)
        {
            string label = pending.Dequeue();
            if (!reachable.Add(label))
                continue;
            if (!byLabel.TryGetValue(label, out MirBlock? block))
                continue;

            foreach (string successor in EnumerateSuccessors(block.Terminator))
                pending.Enqueue(successor);
        }

        return reachable;
    }

    private static IEnumerable<string> EnumerateSuccessors(MirTerminator terminator)
    {
        switch (terminator)
        {
            case MirGotoTerminator mirGoto:
                yield return mirGoto.TargetLabel;
                break;

            case MirBranchTerminator branch:
                yield return branch.TrueLabel;
                yield return branch.FalseLabel;
                break;
        }
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

        return false;
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

    private static MirValueId ResolveAlias(MirValueId value, IReadOnlyDictionary<MirValueId, MirValueId> aliases)
    {
        MirValueId current = value;
        while (aliases.TryGetValue(current, out MirValueId next) && next != current)
            current = next;
        return current;
    }

    private static Dictionary<MirValueId, MirValueId> ResolveAliasMap(IReadOnlyDictionary<MirValueId, MirValueId> aliases)
    {
        Dictionary<MirValueId, MirValueId> resolved = [];
        foreach ((MirValueId key, MirValueId value) in aliases)
            resolved[key] = ResolveAlias(value, aliases);
        return resolved;
    }
}
