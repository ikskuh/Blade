using System;
using System.Collections.Generic;
using System.Linq;
using Blade;
using Blade.Semantics;

namespace Blade.IR.Asm;

/// <summary>
/// Post-register-allocation legalization pass for ASMIR (whole-program).
/// Handles:
/// 1. Immediate range checks — inserts AUGS/AUGD for values that don't fit
///    in their encoded operand field, or promotes to a shared constant register
///    when the same bit pattern appears multiple times.
/// 2. Operates on the entire module, not per-function, since code + data
///    share the same 512-long COG register file.
/// </summary>
public static class AsmLegalizer
{
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
        Dictionary<uint, AsmSharedConstantSymbol> constantRegisters = [];
        foreach ((uint value, int count) in immediateUseCounts.OrderBy(static pair => pair.Key))
        {
            if (count >= 2)
                constantRegisters[value] = new AsmSharedConstantSymbol(value);
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
            List<AsmNode> extendedNodes = new(entry.Nodes.Count + (constantRegisters.Count * 2));
            extendedNodes.AddRange(entry.Nodes);

            extendedNodes.Add(new AsmSectionNode(AsmStorageSection.Constant));
            foreach ((uint value, AsmSharedConstantSymbol label) in constantRegisters.OrderBy(static pair => pair.Key))
            {
                extendedNodes.Add(new AsmLabelNode(label.Name));
                extendedNodes.Add(new AsmDataNode(AsmDataDirective.Long, value, useHexFormat: true));
            }

            functions[entryIndex] = new AsmFunction(entry, extendedNodes);
        }

        return new AsmModule(module.StoragePlaces, functions);
    }

    private static void CountLargeImmediates(
        AsmInstructionNode instruction,
        Dictionary<uint, int> counts)
    {
        for (int operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
        {
            AsmOperand operand = instruction.Operands[operandIndex];
            if (operand is AsmImmediateOperand imm)
            {
                P2InstructionOperandInfo operandInfo = P2InstructionMetadata.GetOperandInfo(
                    instruction.Mnemonic,
                    instruction.Operands.Count,
                    operandIndex);
                if (!CanUseSharedConstant(operandInfo))
                    continue;

                uint uval = unchecked((uint)imm.Value);
                if (!FitsInOperandField(uval, operandInfo.BitWidth))
                {
                    counts.TryGetValue(uval, out int existing);
                    counts[uval] = existing + 1;
                }
            }
        }
    }

    private static AsmFunction LegalizeFunction(
        AsmFunction function,
        Dictionary<uint, AsmSharedConstantSymbol> constantRegisters)
    {
        List<AsmNode> nodes = new(function.Nodes.Count);

        foreach (AsmNode node in function.Nodes)
        {
            if (node is AsmInstructionNode instruction)
                LegalizeInstruction(nodes, instruction, constantRegisters);
            else
                nodes.Add(node);
        }

        return new AsmFunction(function, nodes);
    }

    private static void LegalizeInstruction(
        List<AsmNode> nodes,
        AsmInstructionNode instruction,
        Dictionary<uint, AsmSharedConstantSymbol> constantRegisters)
    {
        bool modified = false;
        List<AsmOperand> newOperands = new(instruction.Operands.Count);
        List<(P2Mnemonic Opcode, long Value)> prefixes = [];

        for (int i = 0; i < instruction.Operands.Count; i++)
        {
            AsmOperand operand = instruction.Operands[i];
            P2InstructionOperandInfo operandInfo = P2InstructionMetadata.GetOperandInfo(
                instruction.Mnemonic,
                instruction.Operands.Count,
                i);

            if (operand is AsmImmediateOperand imm)
            {
                if (!operandInfo.SupportsImmediateSyntax || operandInfo.BitWidth <= 0)
                    throw new InvalidOperationException($"Instruction '{instruction.Opcode}' operand {i} does not support immediate syntax.");

                uint uval = unchecked((uint)imm.Value);

                if (!FitsInOperandField(uval, operandInfo.BitWidth))
                {
                    // Check if this value has a shared constant register
                    if (CanUseSharedConstant(operandInfo)
                        && constantRegisters.TryGetValue(uval, out AsmSharedConstantSymbol? constLabel))
                    {
                        // Replace immediate with reference to constant register
                        newOperands.Add(new AsmSymbolOperand(constLabel, AsmSymbolAddressingMode.Register));
                        modified = true;
                        continue;
                    }

                    if (operandInfo.AugPrefix == P2AugPrefixKind.None)
                    {
                        throw new InvalidOperationException(
                            $"Immediate value #{imm.Value} does not fit operand {i} of instruction '{instruction.Opcode}' and cannot be AUG-extended.");
                    }

                    prefixes.Add((GetAugOpcode(operandInfo.AugPrefix), unchecked((long)(uval >> operandInfo.BitWidth))));
                    long lowImmediateBits = unchecked((long)(uval & GetOperandMask(operandInfo.BitWidth)));
                    newOperands.Add(new AsmImmediateOperand(lowImmediateBits));
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

        // Emit AUG prefixes immediately before the instruction in operand order.
        foreach ((P2Mnemonic opcode, long value) in prefixes)
        {
            nodes.Add(new AsmInstructionNode(opcode, [new AsmImmediateOperand(value)]));
        }

        if (modified)
        {
            nodes.Add(new AsmInstructionNode(
                instruction.Mnemonic,
                newOperands,
                instruction.Condition,
                instruction.FlagEffect,
                instruction.IsNonElidable));
        }
        else
        {
            nodes.Add(instruction);
        }
    }

    private static bool CanUseSharedConstant(P2InstructionOperandInfo operandInfo)
    {
        return operandInfo.SupportsImmediateSyntax
            && operandInfo.BitWidth > 0
            && operandInfo.AugPrefix != P2AugPrefixKind.None;
    }

    private static bool FitsInOperandField(uint value, int bitWidth)
    {
        if (bitWidth <= 0)
            return false;

        if (bitWidth >= 32)
            return true;

        return value <= GetOperandMask(bitWidth);
    }

    private static uint GetOperandMask(int bitWidth)
    {
        if (bitWidth <= 0)
            return 0;

        if (bitWidth >= 32)
            return uint.MaxValue;

        return (1u << bitWidth) - 1u;
    }

    private static P2Mnemonic GetAugOpcode(P2AugPrefixKind augPrefix)
    {
        return augPrefix switch
        {
            P2AugPrefixKind.AUGD => P2Mnemonic.AUGD,
            P2AugPrefixKind.AUGS => P2Mnemonic.AUGS,
            _ => throw new InvalidOperationException($"Unsupported AUG prefix kind '{augPrefix}'."),
        };
    }
}
