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
    private readonly record struct ConstantKey(ImageDescriptor Image, uint Value);

    public static AsmModule Legalize(AsmModule module)
    {
        Requires.NotNull(module);

        Dictionary<ConstantKey, int> immediateUseCounts = [];
        foreach (AsmFunction function in module.Functions)
        {
            foreach (AsmNode node in function.Nodes)
            {
                if (node is AsmInstructionNode instruction)
                    CountLargeImmediates(function, instruction, immediateUseCounts);
            }
        }

        Dictionary<ConstantKey, AsmSharedConstantSymbol> constantRegisters = [];
        foreach ((ConstantKey key, int count) in immediateUseCounts.OrderBy(static pair => pair.Key.Image.Task.Name, StringComparer.Ordinal).ThenBy(static pair => pair.Key.Value))
        {
            bool requiresConstantRegister = key.Image.ExecutionMode is AddressSpace.Cog or AddressSpace.Lut
                || count >= 2;
            if (requiresConstantRegister)
                constantRegisters[key] = new AsmSharedConstantSymbol(key.Image, key.Value);
        }

        List<AsmFunction> functions = new(module.Functions.Count);
        foreach (AsmFunction function in module.Functions)
        {
            AsmFunction legalized = LegalizeFunction(function, constantRegisters);
            functions.Add(legalized);
        }

        List<AsmDataBlock> dataBlocks = [.. module.DataBlocks];
        if (constantRegisters.Count > 0)
        {
            List<AsmDataDefinition> constantDefinitions = [];
            foreach ((ConstantKey key, AsmSharedConstantSymbol label) in constantRegisters.OrderBy(static pair => pair.Key.Image.Task.Name, StringComparer.Ordinal).ThenBy(static pair => pair.Key.Value))
            {
                constantDefinitions.Add(new AsmAllocatedStorageDefinition(
                    label,
                    AddressSpace.Cog,
                    BuiltinTypes.U32,
                    [new AsmImmediateOperand((long)key.Value)],
                    useHexFormat: true));
            }

            ReplaceDataBlock(dataBlocks, new AsmDataBlock(AsmDataBlockKind.Constant, constantDefinitions));
        }

        return new AsmModule(module.SourceModule, module.StoragePlaces, dataBlocks, functions);
    }

    private static void ReplaceDataBlock(List<AsmDataBlock> dataBlocks, AsmDataBlock replacement)
    {
        for (int i = 0; i < dataBlocks.Count; i++)
        {
            if (dataBlocks[i].Kind == replacement.Kind)
            {
                dataBlocks[i] = replacement;
                return;
            }
        }

        dataBlocks.Add(replacement);
    }

    private static void CountLargeImmediates(
        AsmFunction function,
        AsmInstructionNode instruction,
        Dictionary<ConstantKey, int> counts)
    {
        for (int operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
        {
            AsmOperand operand = instruction.Operands[operandIndex];
            if (TryGetImmediateValue(operand, out uint uval))
            {
                P2InstructionOperandInfo operandInfo = P2InstructionMetadata.GetOperandInfo(
                    instruction.Mnemonic,
                    instruction.Operands.Count,
                    operandIndex);
                bool canUseSharedConstant = CanUseSharedConstant(operandInfo)
                    || IsIndirectLutPointerOperand(operand);
                if (!canUseSharedConstant)
                    continue;

                if (!FitsInOperandEncoding(instruction, operandIndex, operand, operandInfo, uval))
                {
                    ConstantKey key = new(function.OwningImage, uval);
                    counts.TryGetValue(key, out int existing);
                    counts[key] = existing + 1;
                }
            }
        }
    }

    private static AsmFunction LegalizeFunction(
        AsmFunction function,
        Dictionary<ConstantKey, AsmSharedConstantSymbol> constantRegisters)
    {
        List<AsmNode> nodes = new(function.Nodes.Count);

        foreach (AsmNode node in function.Nodes)
        {
            if (node is AsmInstructionNode instruction)
                LegalizeInstruction(function, nodes, instruction, constantRegisters);
            else
                nodes.Add(node);
        }

        return new AsmFunction(function, nodes);
    }

    private static void LegalizeInstruction(
        AsmFunction function,
        List<AsmNode> nodes,
        AsmInstructionNode instruction,
        Dictionary<ConstantKey, AsmSharedConstantSymbol> constantRegisters)
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

            if (TryGetImmediateValue(operand, out uint uval))
            {
                if (!operandInfo.SupportsImmediateSyntax || operandInfo.BitWidth <= 0)
                    throw new InvalidOperationException($"Instruction '{instruction.Opcode}' operand {i} does not support immediate syntax.");

                if (!FitsInOperandEncoding(instruction, i, operand, operandInfo, uval))
                {
                    ConstantKey constantKey = new(function.OwningImage, uval);
                    bool canUseSharedConstant = CanUseSharedConstant(operandInfo)
                        || IsIndirectLutPointerOperand(operand);
                    if (canUseSharedConstant
                        && constantRegisters.TryGetValue(constantKey, out AsmSharedConstantSymbol? constLabel))
                    {
                        newOperands.Add(new AsmSymbolOperand(constLabel, AsmSymbolAddressingMode.Register));
                        modified = true;
                        continue;
                    }

                    if (operandInfo.AugPrefix == P2AugPrefixKind.None)
                    {
                        throw new InvalidOperationException(
                            $"Immediate value #{GetSignedImmediateValue(operand)} does not fit operand {i} of instruction '{instruction.Opcode}' and cannot be AUG-extended.");
                    }

                    // PASM source accepts the original literal on the AUG* prefix,
                    // but the following instruction operand still has to fit its
                    // encoded field width. The assembler consumes the upper bits
                    // from AUG* and the low bits from the instruction operand.
                    prefixes.Add((GetAugOpcode(operandInfo.AugPrefix), GetSignedImmediateValue(operand)));
                    long lowImmediateBits = uval & GetOperandMask(operandInfo.BitWidth);
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

    private static bool TryGetImmediateValue(AsmOperand operand, out uint value)
    {
        Requires.NotNull(operand);

        if (operand is AsmImmediateOperand immediate)
        {
            value = unchecked((uint)immediate.Value);
            return true;
        }

        if (operand is AsmSymbolOperand
            {
                AddressingMode: AsmSymbolAddressingMode.Immediate,
                Symbol: StoragePlace
                {
                    StorageClass: AddressSpace.Lut,
                    ResolvedLayoutSlot: { Address: var resolvedAddress }
                }
            })
        {
            value = unchecked((uint)(int)resolvedAddress.ToLutAddress());
            return true;
        }

        if (operand is AsmSymbolOperand
            {
                AddressingMode: AsmSymbolAddressingMode.Immediate,
                Symbol: StoragePlace
                {
                    StorageClass: AddressSpace.Lut,
                    FixedAddress: { } fixedAddress
                }
            })
        {
            value = unchecked((uint)(int)fixedAddress.ToLutAddress());
            return true;
        }

        value = 0;
        return false;
    }

    private static long GetSignedImmediateValue(AsmOperand operand)
    {
        Requires.NotNull(operand);

        return operand switch
        {
            AsmImmediateOperand immediate => immediate.Value,
            _ when TryGetImmediateValue(operand, out uint value) => unchecked((int)value),
            _ => throw new InvalidOperationException($"Operand '{operand.GetType().Name}' does not provide an immediate value."),
        };
    }

    private static bool IsIndirectLutPointerOperand(AsmOperand operand)
    {
        Requires.NotNull(operand);
        return operand is AsmSymbolOperand
        {
            AddressingMode: AsmSymbolAddressingMode.Immediate,
            Symbol.SymbolType: SymbolType.LutVariable,
        };
    }

    private static bool FitsInOperandEncoding(
        AsmInstructionNode instruction,
        int operandIndex,
        AsmOperand operand,
        P2InstructionOperandInfo operandInfo,
        uint value)
    {
        Requires.NotNull(instruction);
        Requires.NotNull(operand);

        if (instruction.Mnemonic is P2Mnemonic.RDLUT or P2Mnemonic.WRLUT
            && operandIndex == 1
            && IsIndirectLutPointerOperand(operand))
        {
            return value <= 0xFF;
        }

        return FitsInOperandField(value, operandInfo.BitWidth);
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
            _ => Assert.UnreachableValue<P2Mnemonic>(), // pragma: force-coverage
        };
    }
}
