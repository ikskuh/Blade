using System.Collections.Generic;
using static Blade.IR.Mir.MirOptimizationHelpers;

namespace Blade.IR.Mir.Optimizations;

[MirOptimization("cfg-simplify", Priority = 500)]
public sealed class MirControlFlowSimplification : IMirOptimization
{
    public MirModule? Run(MirModule input)
    {
        Requires.NotNull(input);

        List<MirFunction> functions = new(input.Functions.Count);
        foreach (MirFunction function in input.Functions)
        {
            IReadOnlyList<MirBlock> threaded = ThreadTrivialGotoBlocks(function.Blocks);
            IReadOnlyList<MirBlock> merged = MergeLinearBlocks(threaded);
            functions.Add(new MirFunction(
                function.Symbol,
                function.IsEntryPoint,
                function.ReturnTypes,
                merged,
                function.ReturnSlots));
        }

        MirModule result = new(input.StoragePlaces, functions);
        return MirTextWriter.Write(result) != MirTextWriter.Write(input) ? result : null;
    }

    private static IReadOnlyList<MirBlock> ThreadTrivialGotoBlocks(IReadOnlyList<MirBlock> blocks)
    {
        if (blocks.Count == 0)
            return blocks;

        Dictionary<MirBlockRef, MirBlock> byLabel = [];
        foreach (MirBlock block in blocks)
            byLabel[block.Ref] = block;

        List<MirBlock> rewritten = new(blocks.Count);
        foreach (MirBlock block in blocks)
        {
            MirTerminator terminator = block.Terminator switch
            {
                MirGotoTerminator mirGoto => RewriteGotoThroughTrivialBlocks(mirGoto, byLabel),
                MirBranchTerminator branch => RewriteBranchThroughTrivialBlocks(branch, byLabel),
                _ => block.Terminator,
            };

            rewritten.Add(new MirBlock(block.Ref, block.Parameters, block.Instructions, terminator));
        }

        return rewritten;
    }

    private static MirGotoTerminator RewriteGotoThroughTrivialBlocks(
        MirGotoTerminator terminator,
        IReadOnlyDictionary<MirBlockRef, MirBlock> byLabel)
    {
        (MirBlockRef label, IReadOnlyList<MirValueId> arguments) = ResolveSuccessor(
            terminator.Target,
            terminator.Arguments,
            byLabel);

        if (ReferenceEquals(label, terminator.Target) && ReferenceEquals(arguments, terminator.Arguments))
            return terminator;

        return new MirGotoTerminator(label, arguments, terminator.Span);
    }

    private static MirBranchTerminator RewriteBranchThroughTrivialBlocks(
        MirBranchTerminator terminator,
        IReadOnlyDictionary<MirBlockRef, MirBlock> byLabel)
    {
        (MirBlockRef trueLabel, IReadOnlyList<MirValueId> trueArguments) = ResolveSuccessor(
            terminator.TrueTarget,
            terminator.TrueArguments,
            byLabel);
        (MirBlockRef falseLabel, IReadOnlyList<MirValueId> falseArguments) = ResolveSuccessor(
            terminator.FalseTarget,
            terminator.FalseArguments,
            byLabel);

        if (ReferenceEquals(trueLabel, terminator.TrueTarget)
            && ReferenceEquals(falseLabel, terminator.FalseTarget)
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
            terminator.Span,
            terminator.ConditionFlag);
    }

    private static IReadOnlyList<MirBlock> MergeLinearBlocks(IReadOnlyList<MirBlock> blocks)
    {
        if (blocks.Count == 0)
            return blocks;

        Dictionary<MirBlockRef, MirBlock> byLabel = [];
        foreach (MirBlock block in blocks)
            byLabel[block.Ref] = block;

        Dictionary<MirBlockRef, int> predecessorCounts = ComputePredecessorCounts(blocks);
        HashSet<MirBlockRef> removed = [];
        List<MirBlock> mergedBlocks = new(blocks.Count);
        MirBlockRef entryRef = blocks[0].Ref;

        foreach (MirBlock original in blocks)
        {
            if (removed.Contains(original.Ref))
                continue;

            MirBlock current = original;
            while (TryMergeSuccessor(
                current,
                entryRef,
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
        MirBlockRef entryRef,
        IReadOnlyDictionary<MirBlockRef, MirBlock> byLabel,
        IReadOnlyDictionary<MirBlockRef, int> predecessorCounts,
        ISet<MirBlockRef> removed,
        out MirBlock merged)
    {
        merged = block;
        if (block.Terminator is not MirGotoTerminator gotoTerminator)
            return false;

        if (ReferenceEquals(gotoTerminator.Target, entryRef)
            || ReferenceEquals(gotoTerminator.Target, block.Ref)
            || removed.Contains(gotoTerminator.Target)
            || !byLabel.TryGetValue(gotoTerminator.Target, out MirBlock? target)
            || predecessorCounts.GetValueOrDefault(target.Ref) != 1)
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
        removed.Add(target.Ref);
        merged = new MirBlock(block.Ref, block.Parameters, instructions, terminator);
        return true;
    }
}
