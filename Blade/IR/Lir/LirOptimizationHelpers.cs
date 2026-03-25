using System.Collections.Generic;
using Blade.Semantics;

namespace Blade.IR.Lir;

internal static class LirOptimizationHelpers
{
    internal static bool IsTrivialGotoBlock(LirBlock block)
        => block.Instructions.Count == 0 && block.Terminator is LirGotoTerminator;

    internal static Dictionary<string, int> ComputePredecessorCounts(IReadOnlyList<LirBlock> blocks)
    {
        Dictionary<string, int> counts = [];
        foreach (LirBlock block in blocks)
        {
            foreach (string successor in EnumerateSuccessors(block.Terminator))
                counts[successor] = counts.GetValueOrDefault(successor) + 1;
        }

        return counts;
    }

    internal static IEnumerable<string> EnumerateSuccessors(LirTerminator terminator)
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

    internal static HashSet<string> ComputeReachableBlocks(LirFunction function)
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

    internal static bool TryGetCopyAlias(
        LirInstruction instruction,
        out LirVirtualRegister destination,
        out LirVirtualRegister source)
    {
        destination = default;
        source = default;

        if (instruction is not LirOpInstruction op
            || op.Operation is not LirMovOperation
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

    internal static LirInstruction RewriteInstructionUsesForCopyPropagation(
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

    internal static LirInstruction RewriteInstructionUses(
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
                    op.Operation,
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

    internal static LirTerminator RewriteTerminatorUses(
        LirTerminator terminator,
        IReadOnlyDictionary<LirVirtualRegister, LirVirtualRegister> mapping)
    {
        Dictionary<LirVirtualRegister, LirOperand> operandMapping = [];
        foreach ((LirVirtualRegister key, LirVirtualRegister value) in mapping)
            operandMapping[key] = new LirRegisterOperand(value);
        return RewriteTerminatorUses(terminator, operandMapping);
    }

    internal static LirTerminator RewriteTerminatorUses(
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

    internal static IReadOnlyList<LirOperand> RewriteOperands(
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

    internal static LirOperand RewriteOperand(
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

    internal static IReadOnlyList<LirInlineAsmBinding> RewriteInlineAsmBindings(
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

    internal static IEnumerable<LirVirtualRegister> EnumerateInstructionUses(LirInstruction instruction)
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

    internal static IEnumerable<LirVirtualRegister> EnumerateWrites(LirInstruction instruction)
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

    internal static IEnumerable<LirVirtualRegister> EnumerateTerminatorUses(LirTerminator terminator)
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

    internal static Dictionary<LirVirtualRegister, LirOperand>? CreateParameterMap(
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

    internal static LirVirtualRegister ResolveAlias(
        LirVirtualRegister value,
        IReadOnlyDictionary<LirVirtualRegister, LirVirtualRegister> aliases)
    {
        LirVirtualRegister current = value;
        while (aliases.TryGetValue(current, out LirVirtualRegister next) && next != current)
            current = next;
        return current;
    }

    internal static Dictionary<LirVirtualRegister, LirVirtualRegister> ResolveAliasMap(
        IReadOnlyDictionary<LirVirtualRegister, LirVirtualRegister> aliases)
    {
        Dictionary<LirVirtualRegister, LirVirtualRegister> resolved = [];
        foreach ((LirVirtualRegister key, LirVirtualRegister value) in aliases)
            resolved[key] = ResolveAlias(value, aliases);
        return resolved;
    }

    internal static (string Label, IReadOnlyList<LirOperand> Arguments) ResolveSuccessor(
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
}
