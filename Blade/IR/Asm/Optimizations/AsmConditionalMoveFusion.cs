using System.Collections.Generic;
using Blade;
using static Blade.IR.Asm.AsmOptimizationHelpers;

namespace Blade.IR.Asm.Optimizations;

[AsmOptimization("conditional-move-fusion", Priority = 500)]
public sealed class AsmConditionalMoveFusion : PerFunctionAsmOptimization
{
    protected override AsmFunction? RunOnFunction(AsmFunction input)
    {
        HashSet<string> targetedLabels = CollectJumpTargets(input.Nodes);
        List<AsmNode> nodes = [];
        bool changed = false;

        for (int i = 0; i < input.Nodes.Count;)
        {
            if (i + 2 < input.Nodes.Count
                && input.Nodes[i] is AsmInstructionNode jump
                && jump.Mnemonic == P2Mnemonic.JMP
                && jump.Condition is not null
                && jump.Operands.Count == 1
                && jump.Operands[0] is AsmSymbolOperand target
                && input.Nodes[i + 1] is AsmInstructionNode body
                && !body.IsNonElidable
                && body.Condition is null
                && input.Nodes[i + 2] is AsmLabelNode label
                && label.Name == target.Name
                && !targetedLabels.Contains(label.Name))
            {
                nodes.Add(new AsmInstructionNode(
                    body.Mnemonic,
                    body.Operands,
                    InvertPredicate(jump.Condition.Value),
                    body.FlagEffect,
                    body.IsNonElidable));
                nodes.Add(label);
                changed = true;
                i += 3;
                continue;
            }

            nodes.Add(input.Nodes[i]);
            i++;
        }

        return changed
            ? new AsmFunction(input, nodes)
            : null;
    }
}
