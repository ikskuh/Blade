using System.Collections.Generic;
using Blade;
using static Blade.IR.Asm.AsmOptimizationHelpers;

namespace Blade.IR.Asm.Optimizations;

[AsmOptimization("ret-fusion", Priority = 600)]
public sealed class AsmRetFusion : PerFunctionAsmOptimization
{
    protected override AsmFunction? RunOnFunction(AsmFunction input)
    {
        HashSet<string> targetedLabels = CollectJumpTargets(input.Nodes);
        List<AsmNode> nodes = [];
        bool changed = false;

        for (int i = 0; i < input.Nodes.Count; i++)
        {
            AsmNode node = input.Nodes[i];
            if (node is AsmInstructionNode instruction
                && instruction.Mnemonic == P2Mnemonic.RET
                && instruction.Condition is null
                && instruction.Operands.Count == 0
                && instruction.FlagEffect == P2FlagEffect.None
                && i > 0
                && nodes.Count > 0
                && nodes[^1] is AsmInstructionNode previous
                && !previous.IsNonElidable
                && previous.Condition is null
                && !IsControlFlowInstruction(previous)
                && !EndsWithTargetedLabel(nodes, targetedLabels))
            {
                nodes[^1] = new AsmInstructionNode(
                    previous.Mnemonic,
                    previous.Operands,
                    P2ConditionCode._RET_,
                    previous.FlagEffect,
                    previous.IsNonElidable);
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
