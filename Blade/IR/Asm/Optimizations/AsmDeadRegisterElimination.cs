using System.Collections.Generic;
using static Blade.IR.Asm.AsmOptimizationHelpers;

namespace Blade.IR.Asm.Optimizations;

[AsmOptimization("dce-reg", Priority = 800)]
public sealed class AsmDeadRegisterElimination : PerFunctionAsmOptimization
{
    protected override AsmFunction? RunOnFunction(AsmFunction input)
    {
        if (!UsesNonLinearControlFlow(input))
            return RunStraightLine(input);

        FunctionLiveness liveness = LivenessAnalyzer.Analyze(input);
        List<AsmNode> kept = [];
        bool changed = false;

        for (int i = input.Nodes.Count - 1; i >= 0; i--)
        {
            AsmNode node = input.Nodes[i];
            if (node is AsmInstructionNode instruction)
            {
                IReadOnlySet<VirtualAsmValue> liveAfterInstruction = liveness.LiveRegistersAfterInstruction.TryGetValue(i, out HashSet<VirtualAsmValue>? liveSet)
                    ? liveSet
                    : [];

                if (IsDeadInstruction(instruction, liveAfterInstruction))
                {
                    changed = true;
                    continue;
                }
            }

            kept.Add(node);
        }

        if (!changed)
            return null;

        kept.Reverse();
        return new AsmFunction(input, kept);
    }

    private static AsmFunction? RunStraightLine(AsmFunction input)
    {
        HashSet<VirtualAsmValue> live = [];
        List<AsmNode> kept = [];
        bool changed = false;

        for (int i = input.Nodes.Count - 1; i >= 0; i--)
        {
            AsmNode node = input.Nodes[i];
            if (node is AsmInstructionNode instruction)
            {
                if (IsDeadInstruction(instruction, live))
                {
                    changed = true;
                    continue;
                }

                if (TryGetDefinedRegister(instruction, out VirtualAsmValue? definedValue)
                    && definedValue is not null)
                {
                    live.Remove(definedValue);
                }

                foreach (VirtualAsmValue usedValue in EnumerateUsedRegisters(instruction))
                    live.Add(usedValue);
            }

            kept.Add(node);
        }

        if (!changed)
            return null;

        kept.Reverse();
        return new AsmFunction(input, kept);
    }
}
