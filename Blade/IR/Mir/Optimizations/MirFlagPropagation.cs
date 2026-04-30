using System.Collections.Generic;
using Blade.Semantics;
using Blade.Semantics.Bound;

namespace Blade.IR.Mir.Optimizations;

[MirOptimization("flag-propagation", RunAfterIterations = true)]
public sealed class MirFlagPropagation : IMirOptimization
{
    /// <summary>
    /// Scans each block for branches whose condition is produced by a flag-writing
    /// instruction (inline asm with FlagOutput, or comparison) in the same block.
    /// Sets ConditionFlag on the branch so codegen can use predicated jumps.
    /// </summary>
    public MirModule? Run(MirModule input)
    {
        Requires.NotNull(input);

        List<MirFunction> functions = new(input.Functions.Count);
        bool anyChanged = false;

        foreach (MirFunction function in input.Functions)
        {
            // Build a combined flag map: function-level annotations + per-block analysis.
            Dictionary<MirValueId, MirFlag> flagMap = new(function.FlagValues);

            // Also discover flag-producing instructions within each block.
            foreach (MirBlock block in function.Blocks)
            {
                foreach (MirInstruction instruction in block.Instructions)
                {
                    if (instruction is MirInlineAsmInstruction asm && asm.FlagOutput is not null && asm.Result is MirValueId asmResult)
                    {
                        MirFlag flag = asm.FlagOutput == InlineAsmFlagOutput.C ? MirFlag.C : MirFlag.Z;
                        flagMap[asmResult] = flag;
                    }
                    else if (instruction is MirBinaryInstruction binary && binary.Result is MirValueId binResult)
                    {
                        MirFlag? flag = binary.Operator switch
                        {
                            BoundBinaryOperatorKind.Equals => MirFlag.Z,
                            BoundBinaryOperatorKind.NotEquals => MirFlag.NZ,
                            BoundBinaryOperatorKind.Less => MirFlag.C,
                            BoundBinaryOperatorKind.LessOrEqual => MirFlag.NC,
                            BoundBinaryOperatorKind.Greater => MirFlag.C,
                            BoundBinaryOperatorKind.GreaterOrEqual => MirFlag.NC,
                            _ => null,
                        };

                        if (flag is not null)
                            flagMap[binResult] = flag.Value;
                    }
                }
            }

            PropagateFlagsThroughBlockParameters(function, flagMap);

            // Now rewrite branches that consume flag values.
            List<MirBlock> blocks = new(function.Blocks.Count);
            foreach (MirBlock block in function.Blocks)
            {
                if (block.Terminator is MirBranchTerminator branch
                    && branch.ConditionFlag is null
                    && flagMap.TryGetValue(branch.Condition, out MirFlag condFlag))
                {
                    MirBranchTerminator updated = new(
                        branch.Condition,
                        branch.TrueTarget,
                        branch.FalseTarget,
                        branch.TrueArguments,
                        branch.FalseArguments,
                        branch.Span,
                        condFlag);
                    blocks.Add(new MirBlock(block.Ref, block.Parameters, block.Instructions, updated));
                    anyChanged = true;
                }
                else
                {
                    blocks.Add(block);
                }
            }

            functions.Add(new MirFunction(
                function.Symbol,
                function.IsEntryPoint,
                function.ReturnTypes,
                blocks,
                function.ReturnSlots,
                flagMap));
        }

        return anyChanged ? new MirModule(input.Image, input.StoragePlaces, input.StorageDefinitions, functions) : null;
    }

    private static void PropagateFlagsThroughBlockParameters(
        MirFunction function,
        Dictionary<MirValueId, MirFlag> flagMap)
    {
        bool changed;
        do
        {
            changed = false;

            foreach (MirBlock block in function.Blocks)
            {
                for (int parameterIndex = 0; parameterIndex < block.Parameters.Count; parameterIndex++)
                {
                    MirValueId parameterValue = block.Parameters[parameterIndex].Value;
                    if (flagMap.ContainsKey(parameterValue))
                        continue;

                    if (!TryGetIncomingParameterFlag(function, block.Ref, parameterIndex, flagMap, out MirFlag parameterFlag))
                        continue;

                    flagMap[parameterValue] = parameterFlag;
                    changed = true;
                }
            }
        }
        while (changed);
    }

    private static bool TryGetIncomingParameterFlag(
        MirFunction function,
        MirBlockRef target,
        int parameterIndex,
        IReadOnlyDictionary<MirValueId, MirFlag> flagMap,
        out MirFlag parameterFlag)
    {
        parameterFlag = default;
        bool sawIncomingArgument = false;

        foreach (MirBlock predecessor in function.Blocks)
        {
            foreach (IReadOnlyList<MirValueId> arguments in GetArgumentsForSuccessor(predecessor.Terminator, target))
            {
                if (parameterIndex >= arguments.Count)
                    return false;

                MirValueId argument = arguments[parameterIndex];
                if (!flagMap.TryGetValue(argument, out MirFlag incomingFlag))
                    return false;

                if (!sawIncomingArgument)
                {
                    parameterFlag = incomingFlag;
                    sawIncomingArgument = true;
                    continue;
                }

                if (parameterFlag != incomingFlag)
                    return false;
            }
        }

        return sawIncomingArgument;
    }

    private static IEnumerable<IReadOnlyList<MirValueId>> GetArgumentsForSuccessor(MirTerminator terminator, MirBlockRef target)
    {
        switch (terminator)
        {
            case MirGotoTerminator mirGoto when mirGoto.Target == target:
                yield return mirGoto.Arguments;
                yield break;

            case MirBranchTerminator branch:
                if (branch.TrueTarget == target)
                    yield return branch.TrueArguments;

                if (branch.FalseTarget == target)
                    yield return branch.FalseArguments;

                yield break;
        }
    }
}
