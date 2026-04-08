using System.Collections.Generic;
using Blade.Semantics;

namespace Blade.IR.Mir;

internal static class MirOptimizationHelpers
{
    internal static bool IsTrivialGotoBlock(MirBlock block)
        => block.Instructions.Count == 0 && block.Terminator is MirGotoTerminator;

    internal static Dictionary<MirBlockRef, int> ComputePredecessorCounts(IReadOnlyList<MirBlock> blocks)
    {
        Dictionary<MirBlockRef, int> counts = [];
        foreach (MirBlock block in blocks)
        {
            foreach (MirBlockRef successor in EnumerateSuccessors(block.Terminator))
                counts[successor] = counts.GetValueOrDefault(successor) + 1;
        }

        return counts;
    }

    internal static IEnumerable<MirBlockRef> EnumerateSuccessors(MirTerminator terminator)
    {
        switch (terminator)
        {
            case MirGotoTerminator mirGoto:
                yield return mirGoto.Target;
                break;

            case MirBranchTerminator branch:
                yield return branch.TrueTarget;
                yield return branch.FalseTarget;
                break;
        }
    }

    internal static HashSet<MirBlockRef> ComputeReachableBlocks(MirFunction function)
    {
        HashSet<MirBlockRef> reachable = [];
        if (function.Blocks.Count == 0)
            return reachable;

        Dictionary<MirBlockRef, MirBlock> byLabel = [];
        foreach (MirBlock block in function.Blocks)
            byLabel[block.Ref] = block;

        Queue<MirBlockRef> pending = new();
        pending.Enqueue(function.Blocks[0].Ref);
        while (pending.Count > 0)
        {
            MirBlockRef blockRef = pending.Dequeue();
            if (!reachable.Add(blockRef))
                continue;
            if (!byLabel.TryGetValue(blockRef, out MirBlock? block))
                continue;

            foreach (MirBlockRef successor in EnumerateSuccessors(block.Terminator))
                pending.Enqueue(successor);
        }

        return reachable;
    }

    internal static Dictionary<MirValueId, MirValueId>? CreateParameterMap(
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

    internal static IReadOnlyList<MirValueId> RewriteValues(
        IReadOnlyList<MirValueId> values,
        IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        List<MirValueId> rewritten = new(values.Count);
        bool changed = false;
        foreach (MirValueId value in values)
        {
            MirValueId mapped = mapping.TryGetValue(value, out MirValueId? replacement) && replacement is not null ? replacement : value;
            rewritten.Add(mapped);
            changed |= mapped != value;
        }

        return changed ? rewritten : values;
    }

    internal static MirInstruction RewriteInstructionUsesForCopyPropagation(
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
                || !mapping.TryGetValue(value, out MirValueId? mapped)
                || mapped is null
                || mapped == value)
            {
                continue;
            }

            rewritten ??= new List<MirInlineAsmBinding>(inlineAsm.Bindings);
            rewritten[i] = new MirInlineAsmBinding(binding.Slot, binding.Symbol, mapped, binding.Place, binding.Access);
        }

        return rewritten is null
            ? instruction
            : new MirInlineAsmInstruction(
                inlineAsm.Volatility,
                inlineAsm.FlagOutput,
                inlineAsm.ParsedLines,
                rewritten,
                inlineAsm.Span);
    }

    internal static IEnumerable<MirValueId> EnumerateWrites(MirInstruction instruction)
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

    internal static MirValueId ResolveAlias(MirValueId value, IReadOnlyDictionary<MirValueId, MirValueId> aliases)
    {
        MirValueId current = value;
        while (aliases.TryGetValue(current, out MirValueId? next) && next is not null && next != current)
            current = next;
        return current;
    }

    internal static Dictionary<MirValueId, MirValueId> ResolveAliasMap(IReadOnlyDictionary<MirValueId, MirValueId> aliases)
    {
        Dictionary<MirValueId, MirValueId> resolved = [];
        foreach ((MirValueId key, MirValueId value) in aliases)
            resolved[key] = ResolveAlias(value, aliases);
        return resolved;
    }

    internal static (MirBlockRef Target, IReadOnlyList<MirValueId> Arguments) ResolveSuccessor(
        MirBlockRef target,
        IReadOnlyList<MirValueId> arguments,
        IReadOnlyDictionary<MirBlockRef, MirBlock> byLabel)
    {
        MirBlockRef currentTarget = target;
        IReadOnlyList<MirValueId> currentArguments = arguments;
        HashSet<MirBlockRef> seen = [];

        while (byLabel.TryGetValue(currentTarget, out MirBlock? block)
            && seen.Add(currentTarget)
            && IsTrivialGotoBlock(block)
            && block.Terminator is MirGotoTerminator next)
        {
            Dictionary<MirValueId, MirValueId>? parameterMap = CreateParameterMap(block.Parameters, currentArguments);
            if (parameterMap is null)
                break;

            currentArguments = RewriteValues(next.Arguments, parameterMap);
            currentTarget = next.Target;
        }

        return (currentTarget, currentArguments);
    }
}
