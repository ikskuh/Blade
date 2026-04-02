using System.Collections.Generic;
using Blade;
using Blade.Semantics;
using static Blade.IR.Asm.AsmOptimizationHelpers;

namespace Blade.IR.Asm.Optimizations;

[AsmOptimization("conditional-move-fusion", Priority = 500)]
public sealed class AsmConditionalMoveFusion : PerFunctionAsmOptimization
{
    protected override AsmFunction? RunOnFunction(AsmFunction input)
    {
        Dictionary<ControlFlowLabelSymbol, int> targetedLabelCounts = CountJumpTargets(input.Nodes);
        List<AsmNode> nodes = [];
        bool changed = false;

        for (int i = 0; i < input.Nodes.Count;)
        {
            if (i + 2 < input.Nodes.Count
                && input.Nodes[i] is AsmInstructionNode jump
                && jump.Mnemonic == P2Mnemonic.JMP
                && jump.Condition is not null
                && jump.Operands.Count == 1
                && jump.Operands[0] is AsmSymbolOperand { Symbol: ControlFlowLabelSymbol target }
                && input.Nodes[i + 1] is AsmInstructionNode body
                && !body.IsNonElidable
                && body.Condition is null
                && input.Nodes[i + 2] is AsmLabelNode label
                && ReferenceEquals(label.Label, target)
                && targetedLabelCounts.GetValueOrDefault(label.Label) == 1)
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

    private static Dictionary<ControlFlowLabelSymbol, int> CountJumpTargets(IReadOnlyList<AsmNode> nodes)
    {
        Dictionary<ControlFlowLabelSymbol, int> counts = [];
        foreach (AsmNode node in nodes)
        {
            if (node is not AsmInstructionNode
                {
                    Mnemonic: P2Mnemonic.JMP,
                    Operands.Count: 1,
                    Operands: [AsmSymbolOperand { Symbol: ControlFlowLabelSymbol target }],
                })
            {
                continue;
            }

            counts[target] = counts.GetValueOrDefault(target) + 1;
        }

        return counts;
    }
}
