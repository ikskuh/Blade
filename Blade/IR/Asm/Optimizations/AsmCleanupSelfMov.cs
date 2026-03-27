using System.Collections.Generic;
using static Blade.IR.Asm.AsmOptimizationHelpers;

namespace Blade.IR.Asm.Optimizations;

[AsmOptimization("cleanup-self-mov", Priority = 200, State = AsmOptimizationState.PreRegAlloc | AsmOptimizationState.PostRegAlloc)]
public sealed class AsmCleanupSelfMov : PerFunctionAsmOptimization
{
    [PublicApi] // TODO: Remove this when the method usage analyzer is fixde
    protected override AsmFunction? RunOnFunction(AsmFunction input)
    {
        List<AsmNode> nodes = [];
        AsmLabelNode? previousLabel = null;
        bool changed = false;

        foreach (AsmNode node in input.Nodes)
        {
            if (node is AsmLabelNode label)
            {
                if (previousLabel is not null && previousLabel.Name == label.Name)
                {
                    changed = true;
                    continue;
                }

                previousLabel = label;
                nodes.Add(label);
                continue;
            }

            previousLabel = null;

            if (node is AsmInstructionNode instruction
                && !instruction.IsNonElidable
                && IsPlainMov(instruction)
                && OperandsEquivalent(instruction.Operands[0], instruction.Operands[1]))
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
