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
                function.Name,
                function.IsEntryPoint,
                function.Kind,
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
            terminator.Span,
            terminator.ConditionFlag);
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
}
