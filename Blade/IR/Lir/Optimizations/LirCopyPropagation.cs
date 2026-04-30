using System.Collections.Generic;
using static Blade.IR.Lir.LirOptimizationHelpers;

namespace Blade.IR.Lir.Optimizations;

[LirOptimization("copy-prop", Priority = 900)]
public sealed class LirCopyPropagation : ILirOptimization
{
    public LirModule? Run(LirModule input)
    {
        Requires.NotNull(input);

        List<LirFunction> functions = new(input.Functions.Count);
        foreach (LirFunction function in input.Functions)
        {
            List<LirBlock> blocks = new(function.Blocks.Count);
            foreach (LirBlock block in function.Blocks)
            {
                Dictionary<LirVirtualRegister, LirVirtualRegister> aliases = [];
                List<LirInstruction> instructions = new(block.Instructions.Count);
                foreach (LirInstruction instruction in block.Instructions)
                {
                    Dictionary<LirVirtualRegister, LirVirtualRegister> mapping = ResolveAliasMap(aliases);
                    LirInstruction rewritten = RewriteInstructionUsesForCopyPropagation(instruction, mapping);
                    instructions.Add(rewritten);

                    if (rewritten.Destination is LirVirtualRegister destination)
                        aliases.Remove(destination);

                    foreach (LirVirtualRegister written in EnumerateWrites(rewritten))
                        aliases.Remove(written);

                    if (TryGetCopyAlias(rewritten, out LirVirtualRegister dest, out LirVirtualRegister source))
                        aliases[dest] = ResolveAlias(source, aliases);
                }

                LirTerminator terminator = RewriteTerminatorUses(block.Terminator, ResolveAliasMap(aliases));
                blocks.Add(new LirBlock(block.Ref, block.Parameters, instructions, terminator));
            }

            functions.Add(new LirFunction(function.SourceFunction, blocks));
        }

        LirModule result = new(input.SourceModule, input.StoragePlaces, input.StorageDefinitions, functions);
        return LirTextWriter.Write(result) != LirTextWriter.Write(input) ? result : null;
    }
}
