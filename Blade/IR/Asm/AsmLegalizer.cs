using System;
using System.Collections.Generic;

namespace Blade.IR.Asm;

/// <summary>
/// Post-register-allocation legalization pass for ASMIR.
/// Handles:
/// 1. Immediate range checks — inserts AUGS/AUGD for values > 9-bit
/// 2. Function size validation — ensures ≤ 511 instructions per function
/// </summary>
public static class AsmLegalizer
{
    /// <summary>Maximum unsigned 9-bit immediate value for P2 S-field.</summary>
    private const int MaxImmediate9Bit = 511;

    public static AsmModule Legalize(AsmModule module)
    {
        List<AsmFunction> functions = new(module.Functions.Count);
        foreach (AsmFunction function in module.Functions)
            functions.Add(LegalizeFunction(function));
        return new AsmModule(functions);
    }

    private static AsmFunction LegalizeFunction(AsmFunction function)
    {
        List<AsmNode> nodes = new(function.Nodes.Count);
        int instructionCount = 0;

        foreach (AsmNode node in function.Nodes)
        {
            if (node is AsmInstructionNode instruction)
            {
                int countBefore = nodes.Count;
                LegalizeInstruction(nodes, instruction);
                // Count newly emitted instruction nodes
                for (int i = countBefore; i < nodes.Count; i++)
                {
                    if (nodes[i] is AsmInstructionNode)
                        instructionCount++;
                }
            }
            else
            {
                nodes.Add(node);
            }
        }

        // Validate function size
        if (instructionCount > 511 && !function.IsEntryPoint)
        {
            throw new InvalidOperationException(
                $"Function '{function.Name}' has {instructionCount} instructions, " +
                "exceeding the P2 COG limit of 511.");
        }

        return new AsmFunction(function.Name, function.IsEntryPoint, function.CcTier, nodes);
    }

    private static void LegalizeInstruction(List<AsmNode> nodes, AsmInstructionNode instruction)
    {
        bool needsAugs = false;
        long augValue = 0;

        for (int i = 0; i < instruction.Operands.Count; i++)
        {
            if (instruction.Operands[i] is AsmImmediateOperand imm)
            {
                long absValue = Math.Abs(imm.Value);
                if (absValue > MaxImmediate9Bit)
                {
                    needsAugs = true;
                    augValue = imm.Value >> 9;
                    break;
                }
            }
        }

        if (needsAugs)
        {
            nodes.Add(new AsmInstructionNode("AUGS",
                [new AsmImmediateOperand(augValue)]));
        }

        nodes.Add(instruction);
    }
}
