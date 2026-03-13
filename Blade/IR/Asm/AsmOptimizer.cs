using System.Collections.Generic;

namespace Blade.IR.Asm;

public static class AsmOptimizer
{
    public static AsmModule Optimize(AsmModule module)
    {
        List<AsmFunction> functions = new(module.Functions.Count);
        foreach (AsmFunction function in module.Functions)
            functions.Add(OptimizeFunction(function));
        return new AsmModule(functions);
    }

    private static AsmFunction OptimizeFunction(AsmFunction function)
    {
        List<AsmNode> nodes = [];
        AsmLabelNode? previousLabel = null;
        foreach (AsmNode node in function.Nodes)
        {
            if (node is AsmLabelNode label)
            {
                if (previousLabel is not null && previousLabel.Name == label.Name)
                    continue;
                previousLabel = label;
                nodes.Add(label);
                continue;
            }

            previousLabel = null;

            // Remove self-MOV: MOV %rN, %rN
            if (node is AsmInstructionNode instruction
                && instruction.Opcode == "MOV"
                && instruction.Operands.Count == 2
                && instruction.Operands[0] is AsmRegisterOperand dest
                && instruction.Operands[1] is AsmRegisterOperand src
                && dest.RegisterId == src.RegisterId
                && instruction.Predicate is null
                && instruction.FlagEffect == AsmFlagEffect.None)
            {
                continue;
            }

            nodes.Add(node);
        }

        return new AsmFunction(function.Name, function.IsEntryPoint, nodes);
    }
}
