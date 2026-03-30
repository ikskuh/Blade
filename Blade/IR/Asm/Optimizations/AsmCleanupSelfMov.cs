using System.Collections.Generic;
using static Blade.IR.Asm.AsmOptimizationHelpers;

namespace Blade.IR.Asm.Optimizations;

[AsmOptimization("cleanup-self-mov", Priority = 200, State = AsmOptimizationState.PreRegAlloc | AsmOptimizationState.PostRegAlloc)]
public sealed class AsmCleanupSelfMov : PerFunctionAsmOptimization
{
    protected override AsmFunction? RunOnFunction(AsmFunction input)
    {
        List<AsmNode> nodes = [];
        AsmLabelNode? previousLabel = null;
        bool changed = false;

        foreach (AsmNode node in input.Nodes)
        {
            if (node is AsmLabelNode label)
            {
                if (previousLabel is not null && ReferenceEquals(previousLabel.Label, label.Label))
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
                && instruction.Mnemonic == P2Mnemonic.MOV
                && instruction.FlagEffect == P2FlagEffect.None
                && instruction.Operands.Count == 2
                && OperandsEquivalent(instruction.Operands[0], instruction.Operands[1]))
            {
                if (instruction.Condition == P2ConditionCode._RET_)
                {
                    nodes.Add(new AsmInstructionNode(P2Mnemonic.RET, []));
                    changed = true;
                    continue;
                }

                if (instruction.IsNonElidable)
                {
                    nodes.Add(node);
                    continue;
                }

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
