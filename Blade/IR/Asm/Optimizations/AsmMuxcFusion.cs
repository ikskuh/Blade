using System.Collections.Generic;
using static Blade.IR.Asm.AsmOptimizationHelpers;

namespace Blade.IR.Asm.Optimizations;

[AsmOptimization("muxc-fusion", Priority = 400)]
public sealed class AsmMuxcFusion : PerFunctionAsmOptimization
{
    protected override AsmFunction? RunOnFunction(AsmFunction input)
    {
        List<AsmNode> nodes = [];
        bool changed = false;

        for (int i = 0; i < input.Nodes.Count;)
        {
            if (i + 1 < input.Nodes.Count
                && input.Nodes[i] is AsmInstructionNode first
                && input.Nodes[i + 1] is AsmInstructionNode second
                && TryFuseMuxPair(first, second, out AsmInstructionNode? fused))
            {
                nodes.Add(fused!);
                changed = true;
                i += 2;
                continue;
            }

            nodes.Add(input.Nodes[i]);
            i++;
        }

        return changed
            ? new AsmFunction(input.Name, input.IsEntryPoint, input.CcTier, nodes)
            : null;
    }
}
