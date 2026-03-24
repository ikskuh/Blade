using System.Collections.Generic;
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
                && instruction.Opcode == "RET"
                && instruction.Predicate is null
                && instruction.Operands.Count == 0
                && instruction.FlagEffect == AsmFlagEffect.None
                && i > 0
                && nodes.Count > 0
                && nodes[^1] is AsmInstructionNode previous
                && !previous.IsNonElidable
                && previous.Predicate is null
                && !IsControlFlowInstruction(previous)
                && !EndsWithTargetedLabel(nodes, targetedLabels))
            {
                nodes[^1] = new AsmInstructionNode(
                    previous.Opcode,
                    previous.Operands,
                    "_RET_",
                    previous.FlagEffect,
                    previous.IsNonElidable);
                changed = true;
                continue;
            }

            nodes.Add(node);
        }

        return changed
            ? new AsmFunction(input.Name, input.IsEntryPoint, input.CcTier, nodes)
            : null;
    }
}
