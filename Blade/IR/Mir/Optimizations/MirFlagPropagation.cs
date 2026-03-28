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

        return anyChanged ? new MirModule(input.StoragePlaces, functions) : null;
    }
}
