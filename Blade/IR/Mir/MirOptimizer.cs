using System;
using System.Collections.Generic;
using Blade;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;

namespace Blade.IR.Mir;

public static class MirOptimizer
{
    public static MirModule Optimize(
        MirModule module,
        int maxIterations,
        IReadOnlyList<string> enabledOptimizations)
    {
        Requires.NotNull(module);
        Requires.NotNull(enabledOptimizations);

        MirModule current = module;
        int iterations = Math.Max(1, maxIterations);
        for (int i = 0; i < iterations; i++)
        {
            string before = MirTextWriter.Write(current);
            foreach (string optimization in enabledOptimizations)
            {
                current = optimization switch
                {
                    "single-callsite-inline" => current,
                    "cost-inline" => MirInliner.InlineCostBased(current, inlineCostThreshold: 12),
                    "const-prop" => RunConstantPropagation(current),
                    "copy-prop" => RunCopyPropagation(current),
                    "cfg-simplify" => RunControlFlowSimplification(current),
                    "dce" => RunDeadCodeElimination(current),
                    _ => current,
                };
            }

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

        return new MirModule(module.StoragePlaces, functions);
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
                    MirInstruction rewritten = RewriteInstructionUsesForCopyPropagation(instruction, mapping);
                    instructions.Add(rewritten);

                    if (rewritten.Result is MirValueId result)
                        aliases.Remove(result);

                    foreach (MirValueId written in EnumerateWrites(rewritten))
                        aliases.Remove(written);

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

        return new MirModule(module.StoragePlaces, functions);
    }

    private static MirModule RunControlFlowSimplification(MirModule module)
    {
        List<MirFunction> functions = new(module.Functions.Count);
        foreach (MirFunction function in module.Functions)
        {
            IReadOnlyList<MirBlock> threaded = ThreadTrivialGotoBlocks(function.Blocks);
            IReadOnlyList<MirBlock> merged = MergeLinearBlocks(threaded);
            functions.Add(new MirFunction(
                function.Name,
                function.IsEntryPoint,
                function.Kind,
                function.ReturnTypes,
                merged));
        }

        return new MirModule(module.StoragePlaces, functions);
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

        return new MirModule(module.StoragePlaces, functions);
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

    private static IReadOnlyList<MirBlock> ThreadTrivialGotoBlocks(IReadOnlyList<MirBlock> blocks)
    {
        if (blocks.Count == 0)
            return blocks;

        Dictionary<string, MirBlock> byLabel = [];
        foreach (MirBlock block in blocks)
            byLabel[block.Label] = block;

        List<MirBlock> rewritten = new(blocks.Count);
        foreach (MirBlock block in blocks)
        {
            MirTerminator terminator = block.Terminator switch
            {
                MirGotoTerminator mirGoto => RewriteGotoThroughTrivialBlocks(mirGoto, byLabel),
                MirBranchTerminator branch => RewriteBranchThroughTrivialBlocks(branch, byLabel),
                _ => block.Terminator,
            };

            rewritten.Add(new MirBlock(block.Label, block.Parameters, block.Instructions, terminator));
        }

        return rewritten;
    }

    private static MirGotoTerminator RewriteGotoThroughTrivialBlocks(
        MirGotoTerminator terminator,
        IReadOnlyDictionary<string, MirBlock> byLabel)
    {
        (string label, IReadOnlyList<MirValueId> arguments) = ResolveSuccessor(
            terminator.TargetLabel,
            terminator.Arguments,
            byLabel);

        if (label == terminator.TargetLabel && ReferenceEquals(arguments, terminator.Arguments))
            return terminator;

        return new MirGotoTerminator(label, arguments, terminator.Span);
    }

    private static MirBranchTerminator RewriteBranchThroughTrivialBlocks(
        MirBranchTerminator terminator,
        IReadOnlyDictionary<string, MirBlock> byLabel)
    {
        (string trueLabel, IReadOnlyList<MirValueId> trueArguments) = ResolveSuccessor(
            terminator.TrueLabel,
            terminator.TrueArguments,
            byLabel);
        (string falseLabel, IReadOnlyList<MirValueId> falseArguments) = ResolveSuccessor(
            terminator.FalseLabel,
            terminator.FalseArguments,
            byLabel);

        if (trueLabel == terminator.TrueLabel
            && falseLabel == terminator.FalseLabel
            && ReferenceEquals(trueArguments, terminator.TrueArguments)
            && ReferenceEquals(falseArguments, terminator.FalseArguments))
        {
            return terminator;
        }

        return new MirBranchTerminator(
            terminator.Condition,
            trueLabel,
            falseLabel,
            trueArguments,
            falseArguments,
            terminator.Span);
    }

    private static (string Label, IReadOnlyList<MirValueId> Arguments) ResolveSuccessor(
        string label,
        IReadOnlyList<MirValueId> arguments,
        IReadOnlyDictionary<string, MirBlock> byLabel)
    {
        string currentLabel = label;
        IReadOnlyList<MirValueId> currentArguments = arguments;
        HashSet<string> seen = [];

        while (byLabel.TryGetValue(currentLabel, out MirBlock? block)
            && seen.Add(currentLabel)
            && IsTrivialGotoBlock(block)
            && block.Terminator is MirGotoTerminator next)
        {
            Dictionary<MirValueId, MirValueId>? parameterMap = CreateParameterMap(block.Parameters, currentArguments);
            if (parameterMap is null)
                break;

            currentArguments = RewriteValues(next.Arguments, parameterMap);
            currentLabel = next.TargetLabel;
        }

        return (currentLabel, currentArguments);
    }

    private static IReadOnlyList<MirBlock> MergeLinearBlocks(IReadOnlyList<MirBlock> blocks)
    {
        if (blocks.Count == 0)
            return blocks;

        Dictionary<string, MirBlock> byLabel = [];
        foreach (MirBlock block in blocks)
            byLabel[block.Label] = block;

        Dictionary<string, int> predecessorCounts = ComputePredecessorCounts(blocks);
        HashSet<string> removed = [];
        List<MirBlock> mergedBlocks = new(blocks.Count);
        string entryLabel = blocks[0].Label;

        foreach (MirBlock original in blocks)
        {
            if (removed.Contains(original.Label))
                continue;

            MirBlock current = original;
            while (TryMergeSuccessor(
                current,
                entryLabel,
                byLabel,
                predecessorCounts,
                removed,
                out MirBlock merged))
            {
                current = merged;
            }

            mergedBlocks.Add(current);
        }

        return mergedBlocks;
    }

    private static bool TryMergeSuccessor(
        MirBlock block,
        string entryLabel,
        IReadOnlyDictionary<string, MirBlock> byLabel,
        IReadOnlyDictionary<string, int> predecessorCounts,
        ISet<string> removed,
        out MirBlock merged)
    {
        merged = block;
        if (block.Terminator is not MirGotoTerminator gotoTerminator)
            return false;

        if (gotoTerminator.TargetLabel == entryLabel
            || gotoTerminator.TargetLabel == block.Label
            || removed.Contains(gotoTerminator.TargetLabel)
            || !byLabel.TryGetValue(gotoTerminator.TargetLabel, out MirBlock? target)
            || predecessorCounts.GetValueOrDefault(target.Label) != 1)
        {
            return false;
        }

        Dictionary<MirValueId, MirValueId>? parameterMap = CreateParameterMap(target.Parameters, gotoTerminator.Arguments);
        if (parameterMap is null)
            return false;

        List<MirInstruction> instructions = new(block.Instructions.Count + target.Instructions.Count);
        instructions.AddRange(block.Instructions);
        foreach (MirInstruction instruction in target.Instructions)
            instructions.Add(instruction.RewriteUses(parameterMap));

        MirTerminator terminator = target.Terminator.RewriteUses(parameterMap);
        removed.Add(target.Label);
        merged = new MirBlock(block.Label, block.Parameters, instructions, terminator);
        return true;
    }

    private static bool IsTrivialGotoBlock(MirBlock block)
        => block.Instructions.Count == 0 && block.Terminator is MirGotoTerminator;

    private static Dictionary<string, int> ComputePredecessorCounts(IReadOnlyList<MirBlock> blocks)
    {
        Dictionary<string, int> counts = [];
        foreach (MirBlock block in blocks)
        {
            foreach (string successor in EnumerateSuccessors(block.Terminator))
                counts[successor] = counts.GetValueOrDefault(successor) + 1;
        }

        return counts;
    }

    private static Dictionary<MirValueId, MirValueId>? CreateParameterMap(
        IReadOnlyList<MirBlockParameter> parameters,
        IReadOnlyList<MirValueId> arguments)
    {
        if (parameters.Count != arguments.Count)
            return null;

        Dictionary<MirValueId, MirValueId> mapping = [];
        for (int i = 0; i < parameters.Count; i++)
            mapping[parameters[i].Value] = arguments[i];
        return mapping;
    }

    private static IReadOnlyList<MirValueId> RewriteValues(
        IReadOnlyList<MirValueId> values,
        IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        List<MirValueId> rewritten = new(values.Count);
        bool changed = false;
        foreach (MirValueId value in values)
        {
            MirValueId mapped = mapping.TryGetValue(value, out MirValueId replacement) ? replacement : value;
            rewritten.Add(mapped);
            changed |= mapped != value;
        }

        return changed ? rewritten : values;
    }

    private static MirInstruction RewriteInstructionUsesForCopyPropagation(
        MirInstruction instruction,
        IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        if (instruction is not MirInlineAsmInstruction inlineAsm)
            return instruction.RewriteUses(mapping);

        List<MirInlineAsmBinding>? rewritten = null;
        for (int i = 0; i < inlineAsm.Bindings.Count; i++)
        {
            MirInlineAsmBinding binding = inlineAsm.Bindings[i];
            if (binding.Access != InlineAsmBindingAccess.Read
                || binding.Value is not MirValueId value
                || !mapping.TryGetValue(value, out MirValueId mapped)
                || mapped == value)
            {
                continue;
            }

            rewritten ??= new List<MirInlineAsmBinding>(inlineAsm.Bindings);
            rewritten[i] = new MirInlineAsmBinding(binding.Name, mapped, binding.Place, binding.Access);
        }

        return rewritten is null
            ? instruction
            : new MirInlineAsmInstruction(
                inlineAsm.Volatility,
                inlineAsm.Body,
                inlineAsm.FlagOutput,
                inlineAsm.ParsedLines,
                rewritten,
                inlineAsm.Span);
    }

    private static IEnumerable<MirValueId> EnumerateWrites(MirInstruction instruction)
    {
        if (instruction is not MirInlineAsmInstruction inlineAsm)
            yield break;

        foreach (MirInlineAsmBinding binding in inlineAsm.Bindings)
        {
            if (InlineAssemblyBindingAnalysis.IncludesWrite(binding.Access)
                && binding.Value is MirValueId value)
            {
                yield return value;
            }
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
