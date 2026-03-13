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

            if (node is AsmInstructionNode instruction
                && instruction.Opcode == "MOV"
                && instruction.Operands.Count == 2
                && instruction.Operands[0] == instruction.Operands[1])
            {
                continue;
            }

            nodes.Add(node);
        }

        return new AsmFunction(function.Name, function.IsEntryPoint, nodes);
    }
}
