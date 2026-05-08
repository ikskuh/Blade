using System.Collections.Generic;
using static Blade.IR.Lir.LirOptimizationHelpers;

namespace Blade.IR.Lir.Optimizations;

[LirOptimization("dce", Priority = 100)]
public sealed class LirDeadCodeElimination : ILirOptimization
{
    public LirModule? Run(LirModule input)
    {
        Requires.NotNull(input);

        List<LirFunction> functions = new(input.Functions.Count);
        foreach (LirFunction function in input.Functions)
        {
            HashSet<LirBlockRef> reachable = ComputeReachableBlocks(function);
            Dictionary<LirBlockRef, LirBlock> reachableBlocks = [];
            foreach (LirBlock block in function.Blocks)
            {
                if (reachable.Contains(block.Ref))
                    reachableBlocks[block.Ref] = block;
            }

            IReadOnlyDictionary<LirBlockRef, HashSet<VirtualLirValue>> liveOutByBlock = ComputeLiveOut(function, reachableBlocks);

            List<LirBlock> blocks = [];
            foreach (LirBlock block in function.Blocks)
            {
                if (!reachable.Contains(block.Ref))
                    continue;

                HashSet<VirtualLirValue> live = new(liveOutByBlock[block.Ref]);
                foreach (VirtualLirValue used in EnumerateTerminatorValueUses(block.Terminator))
                    live.Add(used);

                List<LirInstruction> kept = [];
                for (int i = block.Instructions.Count - 1; i >= 0; i--)
                {
                    LirInstruction instruction = block.Instructions[i];
                    bool keep = instruction.HasSideEffects
                        || instruction.Destination is null
                        || live.Contains(instruction.Destination);

                    if (!keep)
                        continue;

                    kept.Add(instruction);
                    if (instruction.Destination is VirtualLirValue destination)
                        live.Remove(destination);

                    foreach (VirtualLirValue used in EnumerateInstructionValueUses(instruction))
                        live.Add(used);
                }

                kept.Reverse();
                blocks.Add(new LirBlock(block.Ref, block.Parameters, kept, block.Terminator));
            }

            functions.Add(new LirFunction(function.SourceFunction, blocks, function.FlagValues));
        }

        LirModule result = new(input.SourceModule, input.StoragePlaces, input.StorageDefinitions, functions);
        return LirTextWriter.Write(result) != LirTextWriter.Write(input) ? result : null;
    }

    private static IReadOnlyDictionary<LirBlockRef, HashSet<VirtualLirValue>> ComputeLiveOut(
        LirFunction function,
        IReadOnlyDictionary<LirBlockRef, LirBlock> reachableBlocks)
    {
        Dictionary<LirBlockRef, HashSet<VirtualLirValue>> liveInByBlock = [];
        Dictionary<LirBlockRef, HashSet<VirtualLirValue>> liveOutByBlock = [];

        foreach (LirBlock block in function.Blocks)
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
                LirBlock block = function.Blocks[i];
                if (!reachableBlocks.ContainsKey(block.Ref))
                    continue;

                HashSet<VirtualLirValue> nextLiveOut = ComputeBlockLiveOut(block, reachableBlocks, liveInByBlock);
                HashSet<VirtualLirValue> nextLiveIn = ComputeBlockLiveIn(block, nextLiveOut);

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

        return liveOutByBlock;
    }

    private static HashSet<VirtualLirValue> ComputeBlockLiveOut(
        LirBlock block,
        IReadOnlyDictionary<LirBlockRef, LirBlock> reachableBlocks,
        IReadOnlyDictionary<LirBlockRef, HashSet<VirtualLirValue>> liveInByBlock)
    {
        HashSet<VirtualLirValue> liveOut = [];

        switch (block.Terminator)
        {
            case LirGotoTerminator gotoTerminator:
                AddSuccessorLiveValues(liveOut, gotoTerminator.Target, gotoTerminator.Arguments, reachableBlocks, liveInByBlock);
                break;

            case LirBranchTerminator branchTerminator:
                AddSuccessorLiveValues(liveOut, branchTerminator.TrueTarget, branchTerminator.TrueArguments, reachableBlocks, liveInByBlock);
                AddSuccessorLiveValues(liveOut, branchTerminator.FalseTarget, branchTerminator.FalseArguments, reachableBlocks, liveInByBlock);
                break;
        }

        return liveOut;
    }

    private static void AddSuccessorLiveValues(
        ISet<VirtualLirValue> liveOut,
        LirBlockRef successorRef,
        IReadOnlyList<LirOperand> successorArguments,
        IReadOnlyDictionary<LirBlockRef, LirBlock> reachableBlocks,
        IReadOnlyDictionary<LirBlockRef, HashSet<VirtualLirValue>> liveInByBlock)
    {
        if (!reachableBlocks.TryGetValue(successorRef, out LirBlock? successorCandidate)
            || successorCandidate is null
            || !liveInByBlock.TryGetValue(successorRef, out HashSet<VirtualLirValue>? successorLiveInCandidate)
            || successorLiveInCandidate is null)
        {
            return;
        }

        LirBlock successor = successorCandidate;
        HashSet<VirtualLirValue> successorLiveIn = successorLiveInCandidate;
        Dictionary<VirtualLirValue, VirtualLirValue> successorParameterMap = CreateParameterMap(successor.Parameters, successorArguments);

        foreach (VirtualLirValue liveValue in successorLiveIn)
        {
            if (successorParameterMap.TryGetValue(liveValue, out VirtualLirValue? mappedOperand))
            {
                liveOut.Add(mappedOperand);
                continue;
            }

            liveOut.Add(liveValue);
        }
    }

    private static HashSet<VirtualLirValue> ComputeBlockLiveIn(LirBlock block, IReadOnlyCollection<VirtualLirValue> liveOut)
    {
        HashSet<VirtualLirValue> live = new(liveOut);
        foreach (VirtualLirValue used in EnumerateTerminatorValueUses(block.Terminator))
            live.Add(used);

        for (int instructionIndex = block.Instructions.Count - 1; instructionIndex >= 0; instructionIndex--)
        {
            LirInstruction instruction = block.Instructions[instructionIndex];
            if (instruction.Destination is VirtualLirValue destination)
                live.Remove(destination);

            foreach (VirtualLirValue used in EnumerateInstructionValueUses(instruction))
                live.Add(used);
        }

        return live;
    }

    private static Dictionary<VirtualLirValue, VirtualLirValue> CreateParameterMap(
        IReadOnlyList<LirBlockParameter> parameters,
        IReadOnlyList<LirOperand> arguments)
    {
        Dictionary<VirtualLirValue, VirtualLirValue> mapping = [];
        if (parameters.Count != arguments.Count)
            return mapping;

        for (int i = 0; i < parameters.Count; i++)
        {
            VirtualLirValue? value = arguments[i] switch
            {
                LirRegisterOperand register => register.Register,
                LirFlagOperand flag => flag.Flag,
                _ => null,
            };

            if (value is not null)
                mapping[parameters[i].Value] = value;
        }

        return mapping;
    }
}
