using System.Collections.Generic;
using static Blade.IR.Lir.LirOptimizationHelpers;

namespace Blade.IR.Lir.Optimizations;

[LirOptimization("cfg-simplify", Priority = 500)]
public sealed class LirControlFlowSimplification : ILirOptimization
{
    public LirModule? Run(LirModule input)
    {
        Requires.NotNull(input);

        List<LirFunction> functions = new(input.Functions.Count);
        foreach (LirFunction function in input.Functions)
        {
            IReadOnlyList<LirBlock> threaded = ThreadTrivialGotoBlocks(function.Blocks);
            IReadOnlyList<LirBlock> merged = MergeLinearBlocks(threaded);
            functions.Add(new LirFunction(function.SourceFunction, merged));
        }

        LirModule result = new(input.StoragePlaces, functions);
        return LirTextWriter.Write(result) != LirTextWriter.Write(input) ? result : null;
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
}
