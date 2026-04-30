using System.Collections.Generic;
using static Blade.IR.Mir.MirOptimizationHelpers;

namespace Blade.IR.Mir.Optimizations;

[MirOptimization("dce", Priority = 100)]
public sealed class MirDeadCodeElimination : IMirOptimization
{
    public MirModule? Run(MirModule input)
    {
        Requires.NotNull(input);

        List<MirFunction> functions = new(input.Functions.Count);
        foreach (MirFunction function in input.Functions)
        {
            HashSet<MirBlockRef> reachable = ComputeReachableBlocks(function);
            Dictionary<MirBlockRef, MirBlock> reachableBlocks = [];
            foreach (MirBlock block in function.Blocks)
            {
                if (reachable.Contains(block.Ref))
                    reachableBlocks[block.Ref] = block;
            }

            (
                IReadOnlyDictionary<MirBlockRef, HashSet<MirValueId>> _,
                IReadOnlyDictionary<MirBlockRef, HashSet<MirValueId>> liveOutByBlock) = ComputeLiveness(function, reachableBlocks);

            List<MirBlock> blocks = [];
            foreach (MirBlock block in function.Blocks)
            {
                if (!reachable.Contains(block.Ref))
                    continue;

                HashSet<MirValueId> live = new(liveOutByBlock[block.Ref]);
                foreach (MirValueId used in block.Terminator.Uses)
                    live.Add(used);

                List<MirInstruction> kept = [];
                for (int i = block.Instructions.Count - 1; i >= 0; i--)
                {
                    MirInstruction instruction = block.Instructions[i];
                    bool keep = instruction.HasSideEffects
                        || instruction.Result is null
                        || live.Contains(instruction.Result);

                    if (!keep)
                        continue;

                    kept.Add(instruction);
                    if (instruction.Result is MirValueId resultValue)
                        live.Remove(resultValue);

                    foreach (MirValueId used in instruction.Uses)
                        live.Add(used);
                }

                kept.Reverse();
                blocks.Add(new MirBlock(block.Ref, block.Parameters, kept, block.Terminator));
            }

            functions.Add(new MirFunction(
                function.Symbol,
                function.IsEntryPoint,
                function.ReturnTypes,
                blocks,
                function.ReturnSlots,
                function.FlagValues));
        }

        MirModule result = new(input.Image, input.StoragePlaces, input.StorageDefinitions, functions);
        return MirTextWriter.Write(result) != MirTextWriter.Write(input) ? result : null;
    }

    private static (IReadOnlyDictionary<MirBlockRef, HashSet<MirValueId>> LiveIn, IReadOnlyDictionary<MirBlockRef, HashSet<MirValueId>> LiveOut)
        ComputeLiveness(
            MirFunction function,
            IReadOnlyDictionary<MirBlockRef, MirBlock> reachableBlocks)
    {
        Dictionary<MirBlockRef, HashSet<MirValueId>> liveInByBlock = [];
        Dictionary<MirBlockRef, HashSet<MirValueId>> liveOutByBlock = [];

        foreach (MirBlock block in function.Blocks)
        {
            if (!reachableBlocks.ContainsKey(block.Ref))
                continue;

            liveInByBlock[block.Ref] = [];
            liveOutByBlock[block.Ref] = [];
        }

        bool changed;
        do
        {
            changed = false;
            for (int i = function.Blocks.Count - 1; i >= 0; i--)
            {
                MirBlock block = function.Blocks[i];
                if (!reachableBlocks.ContainsKey(block.Ref))
                    continue;

                HashSet<MirValueId> nextLiveOut = ComputeLiveOut(block, reachableBlocks, liveInByBlock);
                HashSet<MirValueId> nextLiveIn = ComputeLiveIn(block, nextLiveOut);

                if (!liveOutByBlock[block.Ref].SetEquals(nextLiveOut))
                {
                    liveOutByBlock[block.Ref] = nextLiveOut;
                    changed = true;
                }

                if (!liveInByBlock[block.Ref].SetEquals(nextLiveIn))
                {
                    liveInByBlock[block.Ref] = nextLiveIn;
                    changed = true;
                }
            }
        }
        while (changed);

        return (liveInByBlock, liveOutByBlock);
    }

    private static HashSet<MirValueId> ComputeLiveOut(
        MirBlock block,
        IReadOnlyDictionary<MirBlockRef, MirBlock> reachableBlocks,
        IReadOnlyDictionary<MirBlockRef, HashSet<MirValueId>> liveInByBlock)
    {
        HashSet<MirValueId> liveOut = [];

        switch (block.Terminator)
        {
            case MirGotoTerminator gotoTerminator:
                AddSuccessorLiveValues(liveOut, gotoTerminator.Target, gotoTerminator.Arguments, reachableBlocks, liveInByBlock);
                break;

            case MirBranchTerminator branchTerminator:
                AddSuccessorLiveValues(liveOut, branchTerminator.TrueTarget, branchTerminator.TrueArguments, reachableBlocks, liveInByBlock);
                AddSuccessorLiveValues(liveOut, branchTerminator.FalseTarget, branchTerminator.FalseArguments, reachableBlocks, liveInByBlock);
                break;
        }

        return liveOut;
    }

    private static void AddSuccessorLiveValues(
        ISet<MirValueId> liveOut,
        MirBlockRef successorRef,
        IReadOnlyList<MirValueId> successorArguments,
        IReadOnlyDictionary<MirBlockRef, MirBlock> reachableBlocks,
        IReadOnlyDictionary<MirBlockRef, HashSet<MirValueId>> liveInByBlock)
    {
        if (!reachableBlocks.TryGetValue(successorRef, out MirBlock? successorCandidate)
            || successorCandidate is null
            || !liveInByBlock.TryGetValue(successorRef, out HashSet<MirValueId>? successorLiveInCandidate)
            || successorLiveInCandidate is null)
        {
            return;
        }

        MirBlock successor = successorCandidate;
        HashSet<MirValueId> successorLiveIn = successorLiveInCandidate;

        Dictionary<MirValueId, MirValueId>? successorParameterMap = CreateParameterMap(successor.Parameters, successorArguments);

        // Successor block parameters are defined at the target block boundary, so edge
        // liveness must translate any live parameter back to the corresponding argument.
        foreach (MirValueId liveValue in successorLiveIn)
        {
            if (successorParameterMap?.GetValueOrDefault(liveValue) is MirValueId mappedArgument)
            {
                liveOut.Add(mappedArgument);
                continue;
            }

            liveOut.Add(liveValue);
        }
    }

    private static HashSet<MirValueId> ComputeLiveIn(MirBlock block, IReadOnlyCollection<MirValueId> liveOut)
    {
        HashSet<MirValueId> live = new(liveOut);
        foreach (MirValueId used in block.Terminator.Uses)
            live.Add(used);

        for (int instructionIndex = block.Instructions.Count - 1; instructionIndex >= 0; instructionIndex--)
        {
            MirInstruction instruction = block.Instructions[instructionIndex];
            if (instruction.Result is MirValueId result)
                live.Remove(result);

            foreach (MirValueId used in instruction.Uses)
                live.Add(used);
        }

        return live;
    }
}
