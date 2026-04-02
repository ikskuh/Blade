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

        LirModule result = new(input.StoragePlaces, input.StorageDefinitions, functions);
        return LirTextWriter.Write(result) != LirTextWriter.Write(input) ? result : null;
    }

    private static IReadOnlyList<LirBlock> ThreadTrivialGotoBlocks(IReadOnlyList<LirBlock> blocks)
    {
        if (blocks.Count == 0)
            return blocks;

        Dictionary<LirBlockRef, LirBlock> byLabel = [];
        foreach (LirBlock block in blocks)
            byLabel[block.Ref] = block;

        List<LirBlock> rewritten = new(blocks.Count);
        foreach (LirBlock block in blocks)
        {
            LirTerminator terminator = block.Terminator switch
            {
                LirGotoTerminator gotoTerminator => RewriteGotoThroughTrivialBlocks(gotoTerminator, byLabel),
                LirBranchTerminator branch => RewriteBranchThroughTrivialBlocks(branch, byLabel),
                _ => block.Terminator,
            };

            rewritten.Add(new LirBlock(block.Ref, block.Parameters, block.Instructions, terminator));
        }

        return rewritten;
    }

    private static LirGotoTerminator RewriteGotoThroughTrivialBlocks(
        LirGotoTerminator terminator,
        IReadOnlyDictionary<LirBlockRef, LirBlock> byLabel)
    {
        (LirBlockRef label, IReadOnlyList<LirOperand> arguments) = ResolveSuccessor(
            terminator.Target,
            terminator.Arguments,
            byLabel);

        if (ReferenceEquals(label, terminator.Target) && ReferenceEquals(arguments, terminator.Arguments))
            return terminator;

        return new LirGotoTerminator(label, arguments, terminator.Span);
    }

    private static LirBranchTerminator RewriteBranchThroughTrivialBlocks(
        LirBranchTerminator terminator,
        IReadOnlyDictionary<LirBlockRef, LirBlock> byLabel)
    {
        (LirBlockRef trueLabel, IReadOnlyList<LirOperand> trueArguments) = ResolveSuccessor(
            terminator.TrueTarget,
            terminator.TrueArguments,
            byLabel);
        (LirBlockRef falseLabel, IReadOnlyList<LirOperand> falseArguments) = ResolveSuccessor(
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

        Dictionary<LirBlockRef, LirBlock> byLabel = [];
        foreach (LirBlock block in blocks)
            byLabel[block.Ref] = block;

        Dictionary<LirBlockRef, int> predecessorCounts = ComputePredecessorCounts(blocks);
        HashSet<LirBlockRef> removed = [];
        List<LirBlock> mergedBlocks = new(blocks.Count);
        LirBlockRef entryRef = blocks[0].Ref;

        foreach (LirBlock original in blocks)
        {
            if (removed.Contains(original.Ref))
                continue;

            LirBlock current = original;
            while (TryMergeSuccessor(
                current,
                entryRef,
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
        LirBlockRef entryRef,
        IReadOnlyDictionary<LirBlockRef, LirBlock> byLabel,
        IReadOnlyDictionary<LirBlockRef, int> predecessorCounts,
        ISet<LirBlockRef> removed,
        out LirBlock merged)
    {
        merged = block;
        if (block.Terminator is not LirGotoTerminator gotoTerminator)
            return false;

        if (ReferenceEquals(gotoTerminator.Target, entryRef)
            || ReferenceEquals(gotoTerminator.Target, block.Ref)
            || removed.Contains(gotoTerminator.Target)
            || !byLabel.TryGetValue(gotoTerminator.Target, out LirBlock? target)
            || predecessorCounts.GetValueOrDefault(target.Ref) != 1)
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
        removed.Add(target.Ref);
        merged = new LirBlock(block.Ref, block.Parameters, instructions, terminator);
        return true;
    }
}
