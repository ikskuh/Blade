using System.Collections.Generic;
using static Blade.IR.Mir.MirOptimizationHelpers;

namespace Blade.IR.Mir.Optimizations;

[MirOptimization("copy-prop", Priority = 700)]
public sealed class MirCopyPropagation : IMirOptimization
{
    public MirModule? Run(MirModule input)
    {
        Requires.NotNull(input);

        List<MirFunction> functions = new(input.Functions.Count);
        foreach (MirFunction function in input.Functions)
        {
            List<MirBlock> blocks = new(function.Blocks.Count);
            foreach (MirBlock block in function.Blocks)
            {
                Dictionary<MirValueId, MirValueId> aliases = [];
                List<MirInstruction> instructions = [];
                foreach (MirInstruction instruction in block.Instructions)
                {
                    Dictionary<MirValueId, MirValueId> mapping = ResolveAliasMap(aliases);
                    MirInstruction rewritten = RewriteInstructionUsesForCopyPropagation(instruction, mapping);
                    instructions.Add(rewritten);

                    if (rewritten.Result is MirValueId result)
                        aliases.Remove(result);

                    foreach (MirValueId written in EnumerateWrites(rewritten))
                        aliases.Remove(written);

                    if (rewritten is MirCopyInstruction copy && rewritten.Result is MirValueId copyResult)
                        aliases[copyResult] = ResolveAlias(copy.Source, aliases);
                }

                MirTerminator terminator = block.Terminator.RewriteUses(ResolveAliasMap(aliases));
                blocks.Add(new MirBlock(block.Ref, block.Parameters, instructions, terminator));
            }

            functions.Add(new MirFunction(
                function.Symbol,
                function.IsEntryPoint,
                function.ReturnTypes,
                blocks,
                function.ReturnSlots,
                function.FlagValues));
        }

        MirModule result2 = new(input.StoragePlaces, input.StorageDefinitions, functions);
        return MirTextWriter.Write(result2) != MirTextWriter.Write(input) ? result2 : null;
    }
}
