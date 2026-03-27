using System.Collections.Generic;
using static Blade.IR.Asm.AsmOptimizationHelpers;

namespace Blade.IR.Asm.Optimizations;

[AsmOptimization("elide-nops", Priority = 300)]
public sealed class AsmElideNops : PerFunctionAsmOptimization
{
    protected override AsmFunction? RunOnFunction(AsmFunction input)
    {
        List<AsmNode> nodes = [];
        bool changed = false;

        foreach (AsmNode node in input.Nodes)
        {
            if (node is AsmInstructionNode instruction && IsSemanticNop(instruction))
            {
                changed = true;
                continue;
            }

            nodes.Add(node);
        }

        return changed
            ? new AsmFunction(input, nodes)
            : null;
    }
}
