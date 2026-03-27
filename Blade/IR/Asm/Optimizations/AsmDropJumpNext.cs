using System.Collections.Generic;
using Blade;
using static Blade.IR.Asm.AsmOptimizationHelpers;

namespace Blade.IR.Asm.Optimizations;

[AsmOptimization("drop-jmp-next", Priority = 700)]
public sealed class AsmDropJumpNext : PerFunctionAsmOptimization
{
    [PublicApi] // TODO: Remove this when the method usage analyzer is fixde
    protected override AsmFunction? RunOnFunction(AsmFunction input)
    {
        List<AsmNode> nodes = [];
        bool changed = false;

        for (int i = 0; i < input.Nodes.Count; i++)
        {
            AsmNode node = input.Nodes[i];
            if (node is AsmInstructionNode instruction
                && !instruction.IsNonElidable
                && instruction.Mnemonic == P2Mnemonic.JMP
                && instruction.Condition is null
                && instruction.Operands.Count == 1
                && instruction.Operands[0] is AsmSymbolOperand target
                && TryGetNextLabel(input.Nodes, i + 1, out string? nextLabel)
                && nextLabel == target.Name)
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
