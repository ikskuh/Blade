using System.Collections.Generic;
using static Blade.IR.Lir.LirOptimizationHelpers;

namespace Blade.IR.Lir.Optimizations;

[LirOptimization("parameter-pruning", Priority = 90)]
public sealed class LirBlockParameterPruning : ILirOptimization
{
    public LirModule? Run(LirModule input)
    {
        Requires.NotNull(input);

        bool anyChanged = false;
        List<LirFunction> functions = new(input.Functions.Count);
        foreach (LirFunction function in input.Functions)
        {
            LirFunction pruned = PruneFunction(function, ref anyChanged);
            functions.Add(pruned);
        }

        return anyChanged
            ? new LirModule(input.SourceModule, input.StoragePlaces, input.StorageDefinitions, functions)
            : null;
    }

    private static LirFunction PruneFunction(LirFunction function, ref bool anyChanged)
    {
        HashSet<LirBlockRef> reachable = ComputeReachableBlocks(function);
        Dictionary<LirBlockRef, LirBlock> reachableBlocks = [];
        foreach (LirBlock block in function.Blocks)
        {
            if (reachable.Contains(block.Ref))
                reachableBlocks[block.Ref] = block;
        }

        (IReadOnlyDictionary<LirBlockRef, HashSet<VirtualLirValue>> liveInByBlock, _) = ComputeLiveness(function, reachableBlocks);
        Dictionary<LirBlockRef, IReadOnlyList<int>> keptParameterIndices = ComputeKeptParameterIndices(function, reachable, liveInByBlock);

        bool functionChanged = false;
        List<LirBlock> rewrittenBlocks = new(function.Blocks.Count);
        foreach (LirBlock block in function.Blocks)
        {
            if (!reachable.Contains(block.Ref))
                continue;

            IReadOnlyList<int> keptIndices = keptParameterIndices[block.Ref];
            IReadOnlyList<LirBlockParameter> rewrittenParameters = FilterParameters(block.Parameters, keptIndices);
            LirTerminator rewrittenTerminator = RewriteTerminatorArguments(block.Terminator, keptParameterIndices);
            functionChanged |= !ReferenceEquals(rewrittenParameters, block.Parameters) || !ReferenceEquals(rewrittenTerminator, block.Terminator);
            rewrittenBlocks.Add(new LirBlock(block.Ref, rewrittenParameters, block.Instructions, rewrittenTerminator));
        }

        if (!functionChanged)
            return function;

        anyChanged = true;
        return new LirFunction(function.SourceFunction, rewrittenBlocks, function.FlagValues);
    }

    private static Dictionary<LirBlockRef, IReadOnlyList<int>> ComputeKeptParameterIndices(
        LirFunction function,
        IReadOnlySet<LirBlockRef> reachable,
        IReadOnlyDictionary<LirBlockRef, HashSet<VirtualLirValue>> liveInByBlock)
    {
        Dictionary<LirBlockRef, IReadOnlyList<int>> keptIndices = [];
        LirBlockRef entryRef = function.Blocks.Count > 0 ? function.Blocks[0].Ref : null!;

        foreach (LirBlock block in function.Blocks)
        {
            if (!reachable.Contains(block.Ref))
                continue;

            if (ReferenceEquals(block.Ref, entryRef))
            {
                keptIndices[block.Ref] = CreateFullIndexList(block.Parameters.Count);
                continue;
            }

            HashSet<VirtualLirValue> liveIn = liveInByBlock[block.Ref];
            List<int> liveParameterIndices = [];
            for (int i = 0; i < block.Parameters.Count; i++)
            {
                if (liveIn.Contains(block.Parameters[i].Value))
                    liveParameterIndices.Add(i);
            }

            keptIndices[block.Ref] = liveParameterIndices;
        }

        return keptIndices;
    }

    private static IReadOnlyList<int> CreateFullIndexList(int count)
    {
        List<int> indices = new(count);
        for (int i = 0; i < count; i++)
            indices.Add(i);
        return indices;
    }

    private static IReadOnlyList<LirBlockParameter> FilterParameters(IReadOnlyList<LirBlockParameter> parameters, IReadOnlyList<int> keptIndices)
    {
        if (parameters.Count == keptIndices.Count)
            return parameters;

        List<LirBlockParameter> filtered = new(keptIndices.Count);
        for (int i = 0; i < keptIndices.Count; i++)
            filtered.Add(parameters[keptIndices[i]]);
        return filtered;
    }

    private static LirTerminator RewriteTerminatorArguments(
        LirTerminator terminator,
        IReadOnlyDictionary<LirBlockRef, IReadOnlyList<int>> keptParameterIndices)
    {
        switch (terminator)
        {
            case LirGotoTerminator gotoTerminator:
            {
                IReadOnlyList<LirOperand> filteredArguments = FilterArguments(gotoTerminator.Arguments, keptParameterIndices[gotoTerminator.Target]);
                return ReferenceEquals(filteredArguments, gotoTerminator.Arguments)
                    ? terminator
                    : new LirGotoTerminator(gotoTerminator.Target, filteredArguments, gotoTerminator.Span);
            }

            case LirBranchTerminator branchTerminator:
            {
                IReadOnlyList<LirOperand> filteredTrueArguments = FilterArguments(branchTerminator.TrueArguments, keptParameterIndices[branchTerminator.TrueTarget]);
                IReadOnlyList<LirOperand> filteredFalseArguments = FilterArguments(branchTerminator.FalseArguments, keptParameterIndices[branchTerminator.FalseTarget]);
                return ReferenceEquals(filteredTrueArguments, branchTerminator.TrueArguments)
                    && ReferenceEquals(filteredFalseArguments, branchTerminator.FalseArguments)
                    ? terminator
                    : new LirBranchTerminator(
                        branchTerminator.Condition,
                        branchTerminator.TrueTarget,
                        branchTerminator.FalseTarget,
                        filteredTrueArguments,
                        filteredFalseArguments,
                        branchTerminator.Span,
                        branchTerminator.ConditionFlag);
            }

            default:
                return terminator;
        }
    }

    private static IReadOnlyList<LirOperand> FilterArguments(IReadOnlyList<LirOperand> arguments, IReadOnlyList<int> keptIndices)
    {
        if (arguments.Count == keptIndices.Count)
            return arguments;

        List<LirOperand> filtered = new(keptIndices.Count);
        for (int i = 0; i < keptIndices.Count; i++)
            filtered.Add(arguments[keptIndices[i]]);
        return filtered;
    }

    private static (IReadOnlyDictionary<LirBlockRef, HashSet<VirtualLirValue>> LiveIn, IReadOnlyDictionary<LirBlockRef, HashSet<VirtualLirValue>> LiveOut)
        ComputeLiveness(
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

                HashSet<VirtualLirValue> nextLiveOut = ComputeLiveOut(block, reachableBlocks, liveInByBlock);
                HashSet<VirtualLirValue> nextLiveIn = ComputeLiveIn(block, nextLiveOut);

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

    private static HashSet<VirtualLirValue> ComputeLiveOut(
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
            if (successorParameterMap.TryGetValue(liveValue, out VirtualLirValue? mappedArgument))
            {
                liveOut.Add(mappedArgument);
                continue;
            }

            liveOut.Add(liveValue);
        }
    }

    private static HashSet<VirtualLirValue> ComputeLiveIn(LirBlock block, IReadOnlyCollection<VirtualLirValue> liveOut)
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