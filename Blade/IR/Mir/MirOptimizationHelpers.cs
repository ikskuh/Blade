using System.Collections.Generic;
using Blade.Semantics;

namespace Blade.IR.Mir;

internal static class MirOptimizationHelpers
{
    internal static bool IsTrivialGotoBlock(MirBlock block)
        => block.Instructions.Count == 0 && block.Terminator is MirGotoTerminator;

    internal static Dictionary<string, int> ComputePredecessorCounts(IReadOnlyList<MirBlock> blocks)
    {
        Dictionary<string, int> counts = [];
        foreach (MirBlock block in blocks)
        {
            foreach (string successor in EnumerateSuccessors(block.Terminator))
                counts[successor] = counts.GetValueOrDefault(successor) + 1;
        }

        return counts;
    }

    internal static IEnumerable<string> EnumerateSuccessors(MirTerminator terminator)
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

    internal static HashSet<string> ComputeReachableBlocks(MirFunction function)
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
            rewritten[i] = new MirInlineAsmBinding(binding.Name, binding.Symbol, mapped, binding.Place, binding.Access);
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

    internal static (string Label, IReadOnlyList<MirValueId> Arguments) ResolveSuccessor(
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
}
