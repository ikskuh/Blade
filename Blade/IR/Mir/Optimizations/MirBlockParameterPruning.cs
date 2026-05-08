using System.Collections.Generic;
using static Blade.IR.Mir.MirOptimizationHelpers;

namespace Blade.IR.Mir.Optimizations;

[MirOptimization("parameter-pruning", Priority = 90)]
public sealed class MirBlockParameterPruning : IMirOptimization
{
    public MirModule? Run(MirModule input)
    {
        Requires.NotNull(input);

        bool anyChanged = false;
        List<MirFunction> functions = new(input.Functions.Count);
        foreach (MirFunction function in input.Functions)
        {
            MirFunction pruned = PruneFunction(function, ref anyChanged);
            functions.Add(pruned);
        }

        return anyChanged
            ? new MirModule(input.Image, input.StoragePlaces, input.StorageDefinitions, functions)
            : null;
    }

    private static MirFunction PruneFunction(MirFunction function, ref bool anyChanged)
    {
        HashSet<MirBlockRef> reachable = ComputeReachableBlocks(function);
        Dictionary<MirBlockRef, MirBlock> reachableBlocks = [];
        foreach (MirBlock block in function.Blocks)
        {
            if (reachable.Contains(block.Ref))
                reachableBlocks[block.Ref] = block;
        }

        (IReadOnlyDictionary<MirBlockRef, HashSet<MirValueId>> liveInByBlock, _) = ComputeLiveness(function, reachableBlocks);
        Dictionary<MirBlockRef, IReadOnlyList<int>> keptParameterIndices = ComputeKeptParameterIndices(function, reachable, liveInByBlock);

        bool functionChanged = false;
        List<MirBlock> rewrittenBlocks = new(function.Blocks.Count);
        foreach (MirBlock block in function.Blocks)
        {
            if (!reachable.Contains(block.Ref))
                continue;

            IReadOnlyList<int> keptIndices = keptParameterIndices[block.Ref];
            IReadOnlyList<MirBlockParameter> rewrittenParameters = FilterParameters(block.Parameters, keptIndices);
            MirTerminator rewrittenTerminator = RewriteTerminatorArguments(block.Terminator, keptParameterIndices);
            functionChanged |= !ReferenceEquals(rewrittenParameters, block.Parameters) || !ReferenceEquals(rewrittenTerminator, block.Terminator);
            rewrittenBlocks.Add(new MirBlock(block.Ref, rewrittenParameters, block.Instructions, rewrittenTerminator));
        }

        if (!functionChanged)
            return function;

        anyChanged = true;
        return new MirFunction(
            function.Symbol,
            function.IsEntryPoint,
            function.ReturnTypes,
            rewrittenBlocks,
            function.ReturnSlots,
            function.FlagValues);
    }

    private static Dictionary<MirBlockRef, IReadOnlyList<int>> ComputeKeptParameterIndices(
        MirFunction function,
        IReadOnlySet<MirBlockRef> reachable,
        IReadOnlyDictionary<MirBlockRef, HashSet<MirValueId>> liveInByBlock)
    {
        Dictionary<MirBlockRef, IReadOnlyList<int>> keptIndices = [];
        MirBlockRef entryRef = function.Blocks.Count > 0 ? function.Blocks[0].Ref : null!;

        foreach (MirBlock block in function.Blocks)
        {
            if (!reachable.Contains(block.Ref))
                continue;

            if (ReferenceEquals(block.Ref, entryRef))
            {
                keptIndices[block.Ref] = CreateFullIndexList(block.Parameters.Count);
                continue;
            }

            HashSet<MirValueId> liveIn = liveInByBlock[block.Ref];
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

    private static IReadOnlyList<MirBlockParameter> FilterParameters(IReadOnlyList<MirBlockParameter> parameters, IReadOnlyList<int> keptIndices)
    {
        if (parameters.Count == keptIndices.Count)
            return parameters;

        List<MirBlockParameter> filtered = new(keptIndices.Count);
        for (int i = 0; i < keptIndices.Count; i++)
            filtered.Add(parameters[keptIndices[i]]);
        return filtered;
    }

    private static MirTerminator RewriteTerminatorArguments(
        MirTerminator terminator,
        IReadOnlyDictionary<MirBlockRef, IReadOnlyList<int>> keptParameterIndices)
    {
        switch (terminator)
        {
            case MirGotoTerminator gotoTerminator:
            {
                IReadOnlyList<MirValueId> filteredArguments = FilterArguments(gotoTerminator.Arguments, keptParameterIndices[gotoTerminator.Target]);
                return ReferenceEquals(filteredArguments, gotoTerminator.Arguments)
                    ? terminator
                    : new MirGotoTerminator(gotoTerminator.Target, filteredArguments, gotoTerminator.Span);
            }

            case MirBranchTerminator branchTerminator:
            {
                IReadOnlyList<MirValueId> filteredTrueArguments = FilterArguments(branchTerminator.TrueArguments, keptParameterIndices[branchTerminator.TrueTarget]);
                IReadOnlyList<MirValueId> filteredFalseArguments = FilterArguments(branchTerminator.FalseArguments, keptParameterIndices[branchTerminator.FalseTarget]);
                return ReferenceEquals(filteredTrueArguments, branchTerminator.TrueArguments)
                    && ReferenceEquals(filteredFalseArguments, branchTerminator.FalseArguments)
                    ? terminator
                    : new MirBranchTerminator(
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

    private static IReadOnlyList<MirValueId> FilterArguments(IReadOnlyList<MirValueId> arguments, IReadOnlyList<int> keptIndices)
    {
        if (arguments.Count == keptIndices.Count)
            return arguments;

        List<MirValueId> filtered = new(keptIndices.Count);
        for (int i = 0; i < keptIndices.Count; i++)
            filtered.Add(arguments[keptIndices[i]]);
        return filtered;
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