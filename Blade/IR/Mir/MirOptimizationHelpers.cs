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
        IReadOnlyDictionary<MirBlockRef, MirBlock> byLabel,
        IReadOnlyDictionary<MirBlockRef, int> predecessorCounts,
        MirBlockRef entryRef)
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
            if (!CanThreadThroughTrivialBlock(block, byLabel, predecessorCounts, entryRef))
                break;

            currentArguments = RewriteValues(next.Arguments, parameterMap);
            currentTarget = next.Target;
        }

        return (currentTarget, currentArguments);
    }

    private static bool CanThreadThroughTrivialBlock(
        MirBlock block,
        IReadOnlyDictionary<MirBlockRef, MirBlock> byLabel,
        IReadOnlyDictionary<MirBlockRef, int> predecessorCounts,
        MirBlockRef entryRef)
    {
        if (block.Parameters.Count == 0 || block.Terminator is not MirGotoTerminator gotoTerminator)
            return true;

        HashSet<MirValueId> trackedParameters = [];
        foreach (MirBlockParameter parameter in block.Parameters)
            trackedParameters.Add(parameter.Value);

        MirBlockRef currentTarget = gotoTerminator.Target;
        HashSet<MirBlockRef> seen = [block.Ref];
        while (byLabel.TryGetValue(currentTarget, out MirBlock? currentBlock)
            && seen.Add(currentTarget)
            && IsTrivialGotoBlock(currentBlock)
            && currentBlock.Terminator is MirGotoTerminator currentGoto)
        {
            currentTarget = currentGoto.Target;
        }

        if (!byLabel.TryGetValue(currentTarget, out MirBlock? mergeRoot))
            return true;

        if (ContainsTrackedUses(mergeRoot.Instructions, mergeRoot.Terminator, trackedParameters, mapping: null))
            return false;

        MirBlockRef mergedBlockRef = mergeRoot.Ref;
        MirTerminator currentTerminator = mergeRoot.Terminator;
        HashSet<MirBlockRef> mergeSeen = [mergedBlockRef];
        while (currentTerminator is MirGotoTerminator currentGoto
            && TryGetMergeableLinearSuccessor(
                mergedBlockRef,
                currentGoto.Target,
                entryRef,
                byLabel,
                predecessorCounts,
                out MirBlock successor))
        {
            Dictionary<MirValueId, MirValueId>? parameterMap = CreateParameterMap(successor.Parameters, currentGoto.Arguments);
            if (parameterMap is null)
                return true;

            if (ContainsTrackedUses(successor.Instructions, successor.Terminator, trackedParameters, parameterMap))
                return false;

            mergedBlockRef = successor.Ref;
            if (!mergeSeen.Add(mergedBlockRef))
                return true;

            currentTerminator = successor.Terminator.RewriteUses(parameterMap);
        }

        return true;
    }

    private static bool TryGetMergeableLinearSuccessor(
        MirBlockRef currentBlockRef,
        MirBlockRef targetRef,
        MirBlockRef entryRef,
        IReadOnlyDictionary<MirBlockRef, MirBlock> byLabel,
        IReadOnlyDictionary<MirBlockRef, int> predecessorCounts,
        out MirBlock target)
    {
        target = null!;
        if (ReferenceEquals(targetRef, entryRef) || ReferenceEquals(targetRef, currentBlockRef))
            return false;
        if (!byLabel.TryGetValue(targetRef, out MirBlock? resolvedTarget))
            return false;
        if (predecessorCounts.GetValueOrDefault(resolvedTarget.Ref) != 1)
            return false;

        target = resolvedTarget;
        return true;
    }

    private static bool ContainsTrackedUses(
        IReadOnlyList<MirInstruction> instructions,
        MirTerminator terminator,
        IReadOnlySet<MirValueId> trackedParameters,
        IReadOnlyDictionary<MirValueId, MirValueId>? mapping)
    {
        foreach (MirInstruction instruction in instructions)
        {
            MirInstruction rewritten = mapping is null ? instruction : instruction.RewriteUses(mapping);
            if (ContainsTrackedUses(rewritten.Uses, trackedParameters))
                return true;
        }

        MirTerminator rewrittenTerminator = mapping is null ? terminator : terminator.RewriteUses(mapping);
        return ContainsTrackedUses(rewrittenTerminator.Uses, trackedParameters);
    }

    private static bool ContainsTrackedUses(IReadOnlyList<MirValueId> uses, IReadOnlySet<MirValueId> trackedParameters)
    {
        foreach (MirValueId use in uses)
        {
            if (trackedParameters.Contains(use))
                return true;
        }

        return false;
    }
}
