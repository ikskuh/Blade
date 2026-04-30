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

            IReadOnlyDictionary<LirBlockRef, HashSet<LirVirtualRegister>> liveOutByBlock = ComputeLiveOut(function, reachableBlocks);

            List<LirBlock> blocks = [];
            foreach (LirBlock block in function.Blocks)
            {
                if (!reachable.Contains(block.Ref))
                    continue;

                HashSet<LirVirtualRegister> live = new(liveOutByBlock[block.Ref]);
                foreach (LirVirtualRegister used in EnumerateTerminatorUses(block.Terminator))
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
                    if (instruction.Destination is LirVirtualRegister destination)
                        live.Remove(destination);

                    foreach (LirVirtualRegister used in EnumerateInstructionUses(instruction))
                        live.Add(used);
                }

                kept.Reverse();
                blocks.Add(new LirBlock(block.Ref, block.Parameters, kept, block.Terminator));
            }

            functions.Add(new LirFunction(function.SourceFunction, blocks));
        }

        LirModule result = new(input.SourceModule, input.StoragePlaces, input.StorageDefinitions, functions);
        return LirTextWriter.Write(result) != LirTextWriter.Write(input) ? result : null;
    }

    private static IReadOnlyDictionary<LirBlockRef, HashSet<LirVirtualRegister>> ComputeLiveOut(
        LirFunction function,
        IReadOnlyDictionary<LirBlockRef, LirBlock> reachableBlocks)
    {
        Dictionary<LirBlockRef, HashSet<LirVirtualRegister>> liveInByBlock = [];
        Dictionary<LirBlockRef, HashSet<LirVirtualRegister>> liveOutByBlock = [];

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

                HashSet<LirVirtualRegister> nextLiveOut = ComputeBlockLiveOut(block, reachableBlocks, liveInByBlock);
                HashSet<LirVirtualRegister> nextLiveIn = ComputeBlockLiveIn(block, nextLiveOut);

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

    private static HashSet<LirVirtualRegister> ComputeBlockLiveOut(
        LirBlock block,
        IReadOnlyDictionary<LirBlockRef, LirBlock> reachableBlocks,
        IReadOnlyDictionary<LirBlockRef, HashSet<LirVirtualRegister>> liveInByBlock)
    {
        HashSet<LirVirtualRegister> liveOut = [];

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
        ISet<LirVirtualRegister> liveOut,
        LirBlockRef successorRef,
        IReadOnlyList<LirOperand> successorArguments,
        IReadOnlyDictionary<LirBlockRef, LirBlock> reachableBlocks,
        IReadOnlyDictionary<LirBlockRef, HashSet<LirVirtualRegister>> liveInByBlock)
    {
        if (!reachableBlocks.TryGetValue(successorRef, out LirBlock? successorCandidate)
            || successorCandidate is null
            || !liveInByBlock.TryGetValue(successorRef, out HashSet<LirVirtualRegister>? successorLiveInCandidate)
            || successorLiveInCandidate is null)
        {
            return;
        }

        LirBlock successor = successorCandidate;
        HashSet<LirVirtualRegister> successorLiveIn = successorLiveInCandidate;
        Dictionary<LirVirtualRegister, LirOperand> successorParameterMap = CreateParameterMap(successor.Parameters, successorArguments);

        foreach (LirVirtualRegister liveRegister in successorLiveIn)
        {
            if (successorParameterMap.TryGetValue(liveRegister, out LirOperand? mappedOperand)
                && mappedOperand is LirRegisterOperand registerOperand)
            {
                liveOut.Add(registerOperand.Register);
                continue;
            }

            liveOut.Add(liveRegister);
        }
    }

    private static HashSet<LirVirtualRegister> ComputeBlockLiveIn(LirBlock block, IReadOnlyCollection<LirVirtualRegister> liveOut)
    {
        HashSet<LirVirtualRegister> live = new(liveOut);
        foreach (LirVirtualRegister used in EnumerateTerminatorUses(block.Terminator))
            live.Add(used);

        for (int instructionIndex = block.Instructions.Count - 1; instructionIndex >= 0; instructionIndex--)
        {
            LirInstruction instruction = block.Instructions[instructionIndex];
            if (instruction.Destination is LirVirtualRegister destination)
                live.Remove(destination);

            foreach (LirVirtualRegister used in EnumerateInstructionUses(instruction))
                live.Add(used);
        }

        return live;
    }

    private static Dictionary<LirVirtualRegister, LirOperand> CreateParameterMap(
        IReadOnlyList<LirBlockParameter> parameters,
        IReadOnlyList<LirOperand> arguments)
    {
        Dictionary<LirVirtualRegister, LirOperand> mapping = [];
        if (parameters.Count != arguments.Count)
            return mapping;

        for (int i = 0; i < parameters.Count; i++)
            mapping[parameters[i].Register] = arguments[i];

        return mapping;
    }
}
