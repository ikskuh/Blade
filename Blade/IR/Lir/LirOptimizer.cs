using System;
using System.Collections.Generic;
using Blade;
using Blade.Semantics;

namespace Blade.IR.Lir;

public static class LirOptimizer
{
    public static LirModule Optimize(
        LirModule module,
        int maxIterations,
        IReadOnlyList<string> enabledOptimizations)
    {
        Requires.NotNull(module);
        Requires.NotNull(enabledOptimizationsule);


        LirModule current = module;
        int iterations = Math.Max(1, maxIterations);
        for (int i = 0; i < iterations; i++)
        {
            string before = LirTextWriter.Write(current);
            foreach (string optimization in enabledOptimizations)
            {
                current = optimization switch
                {
                    "copy-prop" => RunCopyPropagation(current),
                    "cfg-simplify" => RunControlFlowSimplification(current),
                    "dce" => RunDeadCodeElimination(current),
                    _ => current,
                };
            }
            string after = LirTextWriter.Write(current);
            if (before == after)
                break;
        }

        return current;
    }

    private static LirModule RunCopyPropagation(LirModule module)
    {
        List<LirFunction> functions = new(module.Functions.Count);
        foreach (LirFunction function in module.Functions)
        {
            List<LirBlock> blocks = new(function.Blocks.Count);
            foreach (LirBlock block in function.Blocks)
            {
                Dictionary<LirVirtualRegister, LirVirtualRegister> aliases = [];
                List<LirInstruction> instructions = new(block.Instructions.Count);
                foreach (LirInstruction instruction in block.Instructions)
                {
                    Dictionary<LirVirtualRegister, LirVirtualRegister> mapping = ResolveAliasMap(aliases);
                    LirInstruction rewritten = RewriteInstructionUsesForCopyPropagation(instruction, mapping);
                    instructions.Add(rewritten);

                    if (rewritten.Destination is LirVirtualRegister destination)
                        aliases.Remove(destination);

                    foreach (LirVirtualRegister written in EnumerateWrites(rewritten))
                        aliases.Remove(written);

                    if (TryGetCopyAlias(rewritten, out LirVirtualRegister dest, out LirVirtualRegister source))
                        aliases[dest] = ResolveAlias(source, aliases);
                }

                LirTerminator terminator = RewriteTerminatorUses(block.Terminator, ResolveAliasMap(aliases));
                blocks.Add(new LirBlock(block.Label, block.Parameters, instructions, terminator));
            }

            functions.Add(new LirFunction(
                function.Name,
                function.IsEntryPoint,
                function.Kind,
                function.ReturnTypes,
                blocks));
        }

        return new LirModule(module.StoragePlaces, functions);
    }

    private static LirModule RunControlFlowSimplification(LirModule module)
    {
        List<LirFunction> functions = new(module.Functions.Count);
        foreach (LirFunction function in module.Functions)
        {
            IReadOnlyList<LirBlock> threaded = ThreadTrivialGotoBlocks(function.Blocks);
            IReadOnlyList<LirBlock> merged = MergeLinearBlocks(threaded);
            functions.Add(new LirFunction(
                function.Name,
                function.IsEntryPoint,
                function.Kind,
                function.ReturnTypes,
                merged));
        }

        return new LirModule(module.StoragePlaces, functions);
    }

    private static LirModule RunDeadCodeElimination(LirModule module)
    {
        List<LirFunction> functions = new(module.Functions.Count);
        foreach (LirFunction function in module.Functions)
        {
            HashSet<string> reachable = ComputeReachableBlocks(function);
            List<LirBlock> blocks = [];
            foreach (LirBlock block in function.Blocks)
            {
                if (!reachable.Contains(block.Label))
                    continue;

                HashSet<LirVirtualRegister> live = [];
                foreach (LirVirtualRegister used in EnumerateUses(block.Terminator))
                    live.Add(used);

                List<LirInstruction> kept = [];
                for (int i = block.Instructions.Count - 1; i >= 0; i--)
                {
                    LirInstruction instruction = block.Instructions[i];
                    bool keep = instruction.HasSideEffects
                        || instruction.Destination is null
                        || live.Contains(instruction.Destination.Value);

                    if (!keep)
                        continue;

                    kept.Add(instruction);
                    foreach (LirVirtualRegister used in EnumerateUses(instruction))
                        live.Add(used);
                }

                kept.Reverse();
                blocks.Add(new LirBlock(block.Label, block.Parameters, kept, block.Terminator));
            }

            functions.Add(new LirFunction(
                function.Name,
                function.IsEntryPoint,
                function.Kind,
                function.ReturnTypes,
                blocks));
        }

        return new LirModule(module.StoragePlaces, functions);
    }

    private static IReadOnlyList<LirBlock> ThreadTrivialGotoBlocks(IReadOnlyList<LirBlock> blocks)
    {
        if (blocks.Count == 0)
            return blocks;

        Dictionary<string, LirBlock> byLabel = [];
        foreach (LirBlock block in blocks)
            byLabel[block.Label] = block;

        List<LirBlock> rewritten = new(blocks.Count);
        foreach (LirBlock block in blocks)
        {
            LirTerminator terminator = block.Terminator switch
            {
                LirGotoTerminator gotoTerminator => RewriteGotoThroughTrivialBlocks(gotoTerminator, byLabel),
                LirBranchTerminator branch => RewriteBranchThroughTrivialBlocks(branch, byLabel),
                _ => block.Terminator,
            };

            rewritten.Add(new LirBlock(block.Label, block.Parameters, block.Instructions, terminator));
        }

        return rewritten;
    }

    private static LirGotoTerminator RewriteGotoThroughTrivialBlocks(
        LirGotoTerminator terminator,
        IReadOnlyDictionary<string, LirBlock> byLabel)
    {
        (string label, IReadOnlyList<LirOperand> arguments) = ResolveSuccessor(
            terminator.TargetLabel,
            terminator.Arguments,
            byLabel);

        if (label == terminator.TargetLabel && ReferenceEquals(arguments, terminator.Arguments))
            return terminator;

        return new LirGotoTerminator(label, arguments, terminator.Span);
    }

    private static LirBranchTerminator RewriteBranchThroughTrivialBlocks(
        LirBranchTerminator terminator,
        IReadOnlyDictionary<string, LirBlock> byLabel)
    {
        (string trueLabel, IReadOnlyList<LirOperand> trueArguments) = ResolveSuccessor(
            terminator.TrueLabel,
            terminator.TrueArguments,
            byLabel);
        (string falseLabel, IReadOnlyList<LirOperand> falseArguments) = ResolveSuccessor(
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

        return new LirBranchTerminator(
            RewriteOperand(terminator.Condition, new Dictionary<LirVirtualRegister, LirOperand>()),
            trueLabel,
            falseLabel,
            trueArguments,
            falseArguments,
            terminator.Span);
    }

    private static (string Label, IReadOnlyList<LirOperand> Arguments) ResolveSuccessor(
        string label,
        IReadOnlyList<LirOperand> arguments,
        IReadOnlyDictionary<string, LirBlock> byLabel)
    {
        string currentLabel = label;
        IReadOnlyList<LirOperand> currentArguments = arguments;
        HashSet<string> seen = [];

        while (byLabel.TryGetValue(currentLabel, out LirBlock? block)
            && seen.Add(currentLabel)
            && IsTrivialGotoBlock(block)
            && block.Terminator is LirGotoTerminator next)
        {
            Dictionary<LirVirtualRegister, LirOperand>? parameterMap = CreateParameterMap(block.Parameters, currentArguments);
            if (parameterMap is null)
                break;

            currentArguments = RewriteOperands(next.Arguments, parameterMap);
            currentLabel = next.TargetLabel;
        }

        return (currentLabel, currentArguments);
    }

    private static IReadOnlyList<LirBlock> MergeLinearBlocks(IReadOnlyList<LirBlock> blocks)
    {
        if (blocks.Count == 0)
            return blocks;

        Dictionary<string, LirBlock> byLabel = [];
        foreach (LirBlock block in blocks)
            byLabel[block.Label] = block;

        Dictionary<string, int> predecessorCounts = ComputePredecessorCounts(blocks);
        HashSet<string> removed = [];
        List<LirBlock> mergedBlocks = new(blocks.Count);
        string entryLabel = blocks[0].Label;

        foreach (LirBlock original in blocks)
        {
            if (removed.Contains(original.Label))
                continue;

            LirBlock current = original;
            while (TryMergeSuccessor(
                current,
                entryLabel,
                byLabel,
                predecessorCounts,
                removed,
                out LirBlock merged))
            {
                current = merged;
            }

            mergedBlocks.Add(current);
        }

        return mergedBlocks;
    }

    private static bool TryMergeSuccessor(
        LirBlock block,
        string entryLabel,
        IReadOnlyDictionary<string, LirBlock> byLabel,
        IReadOnlyDictionary<string, int> predecessorCounts,
        ISet<string> removed,
        out LirBlock merged)
    {
        merged = block;
        if (block.Terminator is not LirGotoTerminator gotoTerminator)
            return false;

        if (gotoTerminator.TargetLabel == entryLabel
            || gotoTerminator.TargetLabel == block.Label
            || removed.Contains(gotoTerminator.TargetLabel)
            || !byLabel.TryGetValue(gotoTerminator.TargetLabel, out LirBlock? target)
            || predecessorCounts.GetValueOrDefault(target.Label) != 1)
        {
            return false;
        }

        Dictionary<LirVirtualRegister, LirOperand>? parameterMap = CreateParameterMap(target.Parameters, gotoTerminator.Arguments);
        if (parameterMap is null)
            return false;

        List<LirInstruction> instructions = new(block.Instructions.Count + target.Instructions.Count);
        instructions.AddRange(block.Instructions);
        foreach (LirInstruction instruction in target.Instructions)
            instructions.Add(RewriteInstructionUses(instruction, parameterMap));

        LirTerminator terminator = RewriteTerminatorUses(target.Terminator, parameterMap);
        removed.Add(target.Label);
        merged = new LirBlock(block.Label, block.Parameters, instructions, terminator);
        return true;
    }

    private static bool IsTrivialGotoBlock(LirBlock block)
        => block.Instructions.Count == 0 && block.Terminator is LirGotoTerminator;

    private static Dictionary<string, int> ComputePredecessorCounts(IReadOnlyList<LirBlock> blocks)
    {
        Dictionary<string, int> counts = [];
        foreach (LirBlock block in blocks)
        {
            foreach (string successor in EnumerateSuccessors(block.Terminator))
                counts[successor] = counts.GetValueOrDefault(successor) + 1;
        }

        return counts;
    }

    private static IEnumerable<string> EnumerateSuccessors(LirTerminator terminator)
    {
        switch (terminator)
        {
            case LirGotoTerminator gotoTerminator:
                yield return gotoTerminator.TargetLabel;
                break;

            case LirBranchTerminator branch:
                yield return branch.TrueLabel;
                yield return branch.FalseLabel;
                break;
        }
    }

    private static HashSet<string> ComputeReachableBlocks(LirFunction function)
    {
        HashSet<string> reachable = [];
        if (function.Blocks.Count == 0)
            return reachable;

        Dictionary<string, LirBlock> byLabel = [];
        foreach (LirBlock block in function.Blocks)
            byLabel[block.Label] = block;

        Queue<string> pending = new();
        pending.Enqueue(function.Blocks[0].Label);
        while (pending.Count > 0)
        {
            string label = pending.Dequeue();
            if (!reachable.Add(label))
                continue;
            if (!byLabel.TryGetValue(label, out LirBlock? block))
                continue;

            foreach (string successor in EnumerateSuccessors(block.Terminator))
                pending.Enqueue(successor);
        }

        return reachable;
    }

    private static bool TryGetCopyAlias(
        LirInstruction instruction,
        out LirVirtualRegister destination,
        out LirVirtualRegister source)
    {
        destination = default;
        source = default;

        if (instruction is not LirOpInstruction op
            || op.Opcode != "mov"
            || op.Predicate is not null
            || op.WritesC
            || op.WritesZ
            || op.HasSideEffects
            || op.Destination is not LirVirtualRegister dest
            || op.Operands.Count != 1
            || op.Operands[0] is not LirRegisterOperand register)
        {
            return false;
        }

        destination = dest;
        source = register.Register;
        return true;
    }

    private static LirInstruction RewriteInstructionUses(
        LirInstruction instruction,
        IReadOnlyDictionary<LirVirtualRegister, LirVirtualRegister> mapping)
    {
        Dictionary<LirVirtualRegister, LirOperand> operandMapping = [];
        foreach ((LirVirtualRegister key, LirVirtualRegister value) in mapping)
            operandMapping[key] = new LirRegisterOperand(value);
        return RewriteInstructionUses(instruction, operandMapping);
    }

    private static LirInstruction RewriteInstructionUsesForCopyPropagation(
        LirInstruction instruction,
        IReadOnlyDictionary<LirVirtualRegister, LirVirtualRegister> mapping)
    {
        Dictionary<LirVirtualRegister, LirOperand> operandMapping = [];
        foreach ((LirVirtualRegister key, LirVirtualRegister value) in mapping)
            operandMapping[key] = new LirRegisterOperand(value);

        if (instruction is not LirInlineAsmInstruction inlineAsm)
            return RewriteInstructionUses(instruction, operandMapping);

        IReadOnlyList<LirInlineAsmBinding> rewrittenBindings =
            RewriteInlineAsmBindings(inlineAsm.Bindings, operandMapping, readOnlyOnly: true);
        if (ReferenceEquals(rewrittenBindings, inlineAsm.Bindings))
            return instruction;

        return new LirInlineAsmInstruction(
            inlineAsm.Volatility,
            inlineAsm.Body,
            inlineAsm.FlagOutput,
            inlineAsm.ParsedLines,
            rewrittenBindings,
            inlineAsm.Span);
    }

    private static LirInstruction RewriteInstructionUses(
        LirInstruction instruction,
        IReadOnlyDictionary<LirVirtualRegister, LirOperand> mapping)
    {
        switch (instruction)
        {
            case LirOpInstruction op:
            {
                IReadOnlyList<LirOperand> rewrittenOperands = RewriteOperands(op.Operands, mapping);
                if (ReferenceEquals(rewrittenOperands, op.Operands))
                    return instruction;

                return new LirOpInstruction(
                    op.Opcode,
                    op.Destination,
                    op.ResultType,
                    rewrittenOperands,
                    op.HasSideEffects,
                    op.Predicate,
                    op.WritesC,
                    op.WritesZ,
                    op.Span);
            }

            case LirInlineAsmInstruction inlineAsm:
            {
                IReadOnlyList<LirInlineAsmBinding> rewrittenBindings =
                    RewriteInlineAsmBindings(inlineAsm.Bindings, mapping, readOnlyOnly: false);
                if (ReferenceEquals(rewrittenBindings, inlineAsm.Bindings))
                    return instruction;

                return new LirInlineAsmInstruction(
                    inlineAsm.Volatility,
                    inlineAsm.Body,
                    inlineAsm.FlagOutput,
                    inlineAsm.ParsedLines,
                    rewrittenBindings,
                    inlineAsm.Span);
            }

            default:
                return instruction;
        }
    }

    private static LirTerminator RewriteTerminatorUses(
        LirTerminator terminator,
        IReadOnlyDictionary<LirVirtualRegister, LirVirtualRegister> mapping)
    {
        Dictionary<LirVirtualRegister, LirOperand> operandMapping = [];
        foreach ((LirVirtualRegister key, LirVirtualRegister value) in mapping)
            operandMapping[key] = new LirRegisterOperand(value);
        return RewriteTerminatorUses(terminator, operandMapping);
    }

    private static LirTerminator RewriteTerminatorUses(
        LirTerminator terminator,
        IReadOnlyDictionary<LirVirtualRegister, LirOperand> mapping)
    {
        switch (terminator)
        {
            case LirGotoTerminator gotoTerminator:
            {
                IReadOnlyList<LirOperand> rewritten = RewriteOperands(gotoTerminator.Arguments, mapping);
                return ReferenceEquals(rewritten, gotoTerminator.Arguments)
                    ? terminator
                    : new LirGotoTerminator(gotoTerminator.TargetLabel, rewritten, gotoTerminator.Span);
            }

            case LirBranchTerminator branch:
            {
                LirOperand condition = RewriteOperand(branch.Condition, mapping);
                IReadOnlyList<LirOperand> rewrittenTrue = RewriteOperands(branch.TrueArguments, mapping);
                IReadOnlyList<LirOperand> rewrittenFalse = RewriteOperands(branch.FalseArguments, mapping);
                if (ReferenceEquals(condition, branch.Condition)
                    && ReferenceEquals(rewrittenTrue, branch.TrueArguments)
                    && ReferenceEquals(rewrittenFalse, branch.FalseArguments))
                {
                    return terminator;
                }

                return new LirBranchTerminator(
                    condition,
                    branch.TrueLabel,
                    branch.FalseLabel,
                    rewrittenTrue,
                    rewrittenFalse,
                    branch.Span);
            }

            case LirReturnTerminator ret:
            {
                IReadOnlyList<LirOperand> rewritten = RewriteOperands(ret.Values, mapping);
                return ReferenceEquals(rewritten, ret.Values)
                    ? terminator
                    : new LirReturnTerminator(rewritten, ret.Span);
            }

            default:
                return terminator;
        }
    }

    private static IReadOnlyList<LirOperand> RewriteOperands(
        IReadOnlyList<LirOperand> operands,
        IReadOnlyDictionary<LirVirtualRegister, LirOperand> mapping)
    {
        List<LirOperand> rewritten = new(operands.Count);
        bool changed = false;
        foreach (LirOperand operand in operands)
        {
            LirOperand mapped = RewriteOperand(operand, mapping);
            rewritten.Add(mapped);
            changed |= !ReferenceEquals(mapped, operand);
        }

        return changed ? rewritten : operands;
    }

    private static LirOperand RewriteOperand(
        LirOperand operand,
        IReadOnlyDictionary<LirVirtualRegister, LirOperand> mapping)
    {
        if (operand is LirRegisterOperand register
            && mapping.TryGetValue(register.Register, out LirOperand? replacement))
        {
            return replacement;
        }

        return operand;
    }

    private static IReadOnlyList<LirInlineAsmBinding> RewriteInlineAsmBindings(
        IReadOnlyList<LirInlineAsmBinding> bindings,
        IReadOnlyDictionary<LirVirtualRegister, LirOperand> mapping,
        bool readOnlyOnly)
    {
        List<LirInlineAsmBinding>? rewritten = null;
        for (int i = 0; i < bindings.Count; i++)
        {
            LirInlineAsmBinding binding = bindings[i];
            if (readOnlyOnly && binding.Access != InlineAsmBindingAccess.Read)
                continue;

            LirOperand operand = RewriteOperand(binding.Operand, mapping);
            if (ReferenceEquals(operand, binding.Operand))
                continue;

            rewritten ??= new List<LirInlineAsmBinding>(bindings);
            rewritten[i] = new LirInlineAsmBinding(binding.Name, operand, binding.Access);
        }

        return rewritten ?? bindings;
    }

    private static IEnumerable<LirVirtualRegister> EnumerateUses(LirInstruction instruction)
    {
        if (instruction is LirInlineAsmInstruction inlineAsm)
        {
            foreach (LirInlineAsmBinding binding in inlineAsm.Bindings)
            {
                if (InlineAssemblyBindingAnalysis.IncludesRead(binding.Access)
                    && binding.Operand is LirRegisterOperand register)
                {
                    yield return register.Register;
                }
            }

            yield break;
        }

        foreach (LirOperand operand in instruction.Operands)
        {
            if (operand is LirRegisterOperand register)
                yield return register.Register;
        }
    }

    private static IEnumerable<LirVirtualRegister> EnumerateWrites(LirInstruction instruction)
    {
        if (instruction is not LirInlineAsmInstruction inlineAsm)
            yield break;

        foreach (LirInlineAsmBinding binding in inlineAsm.Bindings)
        {
            if (InlineAssemblyBindingAnalysis.IncludesWrite(binding.Access)
                && binding.Operand is LirRegisterOperand register)
            {
                yield return register.Register;
            }
        }
    }

    private static IEnumerable<LirVirtualRegister> EnumerateUses(LirTerminator terminator)
    {
        switch (terminator)
        {
            case LirGotoTerminator gotoTerminator:
                foreach (LirOperand operand in gotoTerminator.Arguments)
                {
                    if (operand is LirRegisterOperand register)
                        yield return register.Register;
                }
                break;

            case LirBranchTerminator branch:
                if (branch.Condition is LirRegisterOperand conditionRegister)
                    yield return conditionRegister.Register;
                foreach (LirOperand operand in branch.TrueArguments)
                {
                    if (operand is LirRegisterOperand register)
                        yield return register.Register;
                }
                foreach (LirOperand operand in branch.FalseArguments)
                {
                    if (operand is LirRegisterOperand register)
                        yield return register.Register;
                }
                break;

            case LirReturnTerminator ret:
                foreach (LirOperand operand in ret.Values)
                {
                    if (operand is LirRegisterOperand register)
                        yield return register.Register;
                }
                break;
        }
    }

    private static Dictionary<LirVirtualRegister, LirOperand>? CreateParameterMap(
        IReadOnlyList<LirBlockParameter> parameters,
        IReadOnlyList<LirOperand> arguments)
    {
        if (parameters.Count != arguments.Count)
            return null;

        Dictionary<LirVirtualRegister, LirOperand> mapping = [];
        for (int i = 0; i < parameters.Count; i++)
            mapping[parameters[i].Register] = arguments[i];
        return mapping;
    }

    private static LirVirtualRegister ResolveAlias(
        LirVirtualRegister value,
        IReadOnlyDictionary<LirVirtualRegister, LirVirtualRegister> aliases)
    {
        LirVirtualRegister current = value;
        while (aliases.TryGetValue(current, out LirVirtualRegister next) && next != current)
            current = next;
        return current;
    }

    private static Dictionary<LirVirtualRegister, LirVirtualRegister> ResolveAliasMap(
        IReadOnlyDictionary<LirVirtualRegister, LirVirtualRegister> aliases)
    {
        Dictionary<LirVirtualRegister, LirVirtualRegister> resolved = [];
        foreach ((LirVirtualRegister key, LirVirtualRegister value) in aliases)
            resolved[key] = ResolveAlias(value, aliases);
        return resolved;
    }
}
