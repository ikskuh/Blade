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
                IReadOnlySet<VirtualAsmRegister> liveAfterInstruction = liveness.LiveRegistersAfterInstruction.TryGetValue(i, out HashSet<VirtualAsmRegister>? liveSet)
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
        HashSet<VirtualAsmRegister> live = [];
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

                if (TryGetDefinedRegister(instruction, out VirtualAsmRegister? definedRegister)
                    && definedRegister is not null)
                {
                    live.Remove(definedRegister);
                }

                foreach (VirtualAsmRegister usedRegister in EnumerateUsedRegisters(instruction))
                    live.Add(usedRegister);
            }

            kept.Add(node);
        }

        if (!changed)
            return null;

        kept.Reverse();
        return new AsmFunction(input, kept);
    }
}
