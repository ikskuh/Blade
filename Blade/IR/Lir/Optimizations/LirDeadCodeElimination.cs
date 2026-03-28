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
            List<LirBlock> blocks = [];
            foreach (LirBlock block in function.Blocks)
            {
                if (!reachable.Contains(block.Ref))
                    continue;

                HashSet<LirVirtualRegister> live = [];
                foreach (LirVirtualRegister used in EnumerateTerminatorUses(block.Terminator))
                    live.Add(used);

                List<LirInstruction> kept = [];
                for (int i = block.Instructions.Count - 1; i >= 0; i--)
                {
                    LirInstruction instruction = block.Instructions[i];
                    bool keep = instruction.HasSideEffects
                        || instruction.Destination is null
                        || live.Contains(instruction.Destination.Value);

                    if (!keep)
                        continue;

                    kept.Add(instruction);
                    foreach (LirVirtualRegister used in EnumerateInstructionUses(instruction))
                        live.Add(used);
                }

                kept.Reverse();
                blocks.Add(new LirBlock(block.Ref, block.Parameters, kept, block.Terminator));
            }

            functions.Add(new LirFunction(function.SourceFunction, blocks));
        }

        LirModule result = new(input.StoragePlaces, functions);
        return LirTextWriter.Write(result) != LirTextWriter.Write(input) ? result : null;
    }
}
