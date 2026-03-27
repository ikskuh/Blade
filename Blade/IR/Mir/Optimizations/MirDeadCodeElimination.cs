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
            HashSet<string> reachable = ComputeReachableBlocks(function);
            List<MirBlock> blocks = [];
            foreach (MirBlock block in function.Blocks)
            {
                if (!reachable.Contains(block.Label))
                    continue;

                HashSet<MirValueId> live = [];
                foreach (MirValueId used in block.Terminator.Uses)
                    live.Add(used);

                List<MirInstruction> kept = [];
                for (int i = block.Instructions.Count - 1; i >= 0; i--)
                {
                    MirInstruction instruction = block.Instructions[i];
                    bool keep = instruction.HasSideEffects
                        || instruction.Result is null
                        || live.Contains(instruction.Result.Value);

                    if (!keep)
                        continue;

                    kept.Add(instruction);
                    foreach (MirValueId used in instruction.Uses)
                        live.Add(used);
                }

                kept.Reverse();
                blocks.Add(new MirBlock(block.Label, block.Parameters, kept, block.Terminator));
            }

            functions.Add(new MirFunction(
                function.Symbol,
                function.IsEntryPoint,
                function.ReturnTypes,
                blocks,
                function.ReturnSlots));
        }

        MirModule result = new(input.StoragePlaces, functions);
        return MirTextWriter.Write(result) != MirTextWriter.Write(input) ? result : null;
    }
}
