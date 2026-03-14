using System;
using System.Collections.Generic;
using Blade;

namespace Blade.IR.Asm;

/// <summary>
/// Post-register-allocation legalization pass for ASMIR (whole-program).
/// Handles:
/// 1. Immediate range checks — inserts AUGS/AUGD for values that don't fit
///    in the 9-bit S or D field, or promotes to a shared constant register
///    when the same bit pattern appears multiple times.
/// 2. Operates on the entire module, not per-function, since code + data
///    share the same 512-long COG register file.
/// </summary>
public static class AsmLegalizer
{
    /// <summary>
    /// P2 instruction S-field and D-field are 9 bits wide.
    /// An immediate value fits if its uint32 representation is 0..511.
    /// Negative values like -8 are 0xFFFFFFF8 as uint32, which does NOT fit.
    /// </summary>
    private const uint MaxImmediate9Bit = 511;

    public static AsmModule Legalize(AsmModule module)
    {
        Requires.NotNull(module);

        // First pass: collect all large immediate values across the whole program
        // to decide which ones should share a constant register vs. use AUG.
        Dictionary<uint, int> immediateUseCounts = [];
        foreach (AsmFunction function in module.Functions)
        {
            foreach (AsmNode node in function.Nodes)
            {
                if (node is AsmInstructionNode instruction)
                    CountLargeImmediates(instruction, immediateUseCounts);
            }
        }

        // Immediates used 2+ times → allocate a shared constant register.
        // Immediates used once → use AUG prefix inline.
        Dictionary<uint, string> constantRegisters = [];
        int constantIndex = 0;
        foreach ((uint value, int count) in immediateUseCounts)
        {
            if (count >= 2)
                constantRegisters[value] = $"_const_{constantIndex++}";
        }

        // Second pass: legalize each function
        List<AsmFunction> functions = new(module.Functions.Count);
        foreach (AsmFunction function in module.Functions)
        {
            AsmFunction legalized = LegalizeFunction(function, constantRegisters);
            functions.Add(legalized);
        }

        // Append constant register definitions at the end of the last function
        // (or the entry point) as labeled LONGs
        if (constantRegisters.Count > 0 && functions.Count > 0)
        {
            int entryIndex = functions.Count - 1;
            for (int i = 0; i < functions.Count; i++)
            {
                if (functions[i].IsEntryPoint)
                {
                    entryIndex = i;
                    break;
                }
            }

            AsmFunction entry = functions[entryIndex];
            List<AsmNode> extendedNodes = new(entry.Nodes.Count + constantRegisters.Count * 2);
            extendedNodes.AddRange(entry.Nodes);

            extendedNodes.Add(new AsmCommentNode("--- constant registers ---"));
            foreach ((uint value, string label) in constantRegisters)
            {
                extendedNodes.Add(new AsmLabelNode(label));
                extendedNodes.Add(new AsmDirectiveNode($"LONG ${value:X8}"));
            }

            functions[entryIndex] = new AsmFunction(
                entry.Name, entry.IsEntryPoint, entry.CcTier, extendedNodes);
        }

        return new AsmModule(module.StoragePlaces, functions);
    }

    private static void CountLargeImmediates(
        AsmInstructionNode instruction,
        Dictionary<uint, int> counts)
    {
        foreach (AsmOperand operand in instruction.Operands)
        {
            if (operand is AsmImmediateOperand imm)
            {
                uint uval = unchecked((uint)imm.Value);
                if (uval > MaxImmediate9Bit)
                {
                    counts.TryGetValue(uval, out int existing);
                    counts[uval] = existing + 1;
                }
            }
        }
    }

    private static AsmFunction LegalizeFunction(
        AsmFunction function,
        Dictionary<uint, string> constantRegisters)
    {
        List<AsmNode> nodes = new(function.Nodes.Count);

        foreach (AsmNode node in function.Nodes)
        {
            if (node is AsmInstructionNode instruction)
                LegalizeInstruction(nodes, instruction, constantRegisters);
            else
                nodes.Add(node);
        }

        return new AsmFunction(function.Name, function.IsEntryPoint, function.CcTier, nodes);
    }

    private static void LegalizeInstruction(
        List<AsmNode> nodes,
        AsmInstructionNode instruction,
        Dictionary<uint, string> constantRegisters)
    {
        // P2 instruction format: OPCODE D, S
        // Operand 0 = D-field, Operand 1 = S-field (when both present)
        // Some instructions are single-operand (D-field only, e.g., GETQX D)

        bool modified = false;
        List<AsmOperand> newOperands = new(instruction.Operands.Count);
        string? augPrefix = null;
        long augValue = 0;

        for (int i = 0; i < instruction.Operands.Count; i++)
        {
            AsmOperand operand = instruction.Operands[i];

            if (operand is AsmImmediateOperand imm)
            {
                uint uval = unchecked((uint)imm.Value);

                if (uval > MaxImmediate9Bit)
                {
                    // Check if this value has a shared constant register
                    if (constantRegisters.TryGetValue(uval, out string? constLabel))
                    {
                        // Replace immediate with reference to constant register
                        newOperands.Add(new AsmSymbolOperand(constLabel));
                        modified = true;
                        continue;
                    }

                    // Determine which AUG prefix is needed based on operand slot
                    bool isDField = (i == 0);
                    bool isSField = (i == 1) || (instruction.Operands.Count == 1 && i == 0);

                    // For single-operand instructions, the operand is in S-field
                    // For two-operand: operand[0]=D, operand[1]=S
                    if (instruction.Operands.Count == 1)
                    {
                        // Single operand → typically S-field (e.g., JMP #addr)
                        augPrefix = "AUGS";
                    }
                    else if (i == 0)
                    {
                        // D-field immediate
                        augPrefix = "AUGD";
                    }
                    else
                    {
                        // S-field immediate
                        augPrefix = "AUGS";
                    }

                    augValue = uval >> 9;
                    newOperands.Add(operand);
                    modified = true;
                }
                else
                {
                    newOperands.Add(operand);
                }
            }
            else
            {
                newOperands.Add(operand);
            }
        }

        // Emit AUG prefix if needed (must come immediately before the instruction)
        if (augPrefix is not null)
        {
            nodes.Add(new AsmInstructionNode(augPrefix,
                [new AsmImmediateOperand(augValue)]));
        }

        if (modified)
        {
            nodes.Add(new AsmInstructionNode(
                instruction.Opcode, newOperands, instruction.Predicate, instruction.FlagEffect));
        }
        else
        {
            nodes.Add(instruction);
        }
    }
}
