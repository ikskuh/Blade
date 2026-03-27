using System.Collections.Generic;
using static Blade.IR.Asm.AsmOptimizationHelpers;

namespace Blade.IR.Asm.Optimizations;

[AsmOptimization("dce-reg", Priority = 800)]
public sealed class AsmDeadRegisterElimination : PerFunctionAsmOptimization
{
    [PublicApi] // TODO: Remove this when the method usage analyzer is fixde
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
                IReadOnlySet<int> liveAfterInstruction = liveness.LiveRegistersAfterInstruction.TryGetValue(i, out HashSet<int>? liveSet)
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

    [PublicApi] // TODO: Remove this when the method usage analyzer is fixde
    private static AsmFunction? RunStraightLine(AsmFunction input)
    {
        HashSet<int> live = [];
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

                if (TryGetDefinedRegister(instruction, out int definedRegister))
                    live.Remove(definedRegister);

                foreach (int usedRegister in EnumerateUsedRegisters(instruction))
                    live.Add(usedRegister);
            }
            else if (node is AsmImplicitUseNode implicitUse)
            {
                foreach (AsmOperand operand in implicitUse.Operands)
                {
                    if (operand is AsmRegisterOperand reg)
                        live.Add(reg.RegisterId);
                }
            }

            kept.Add(node);
        }

        if (!changed)
            return null;

        kept.Reverse();
        return new AsmFunction(input, kept);
    }
}
