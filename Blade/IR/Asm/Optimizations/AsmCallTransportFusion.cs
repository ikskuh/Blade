using System.Collections.Generic;
using static Blade.IR.Asm.AsmOptimizationHelpers;

namespace Blade.IR.Asm.Optimizations;

[AsmOptimization("call-transport-fusion", Priority = 650, State = AsmOptimizationState.PostRegAlloc)]
public sealed class AsmCallTransportFusion : PerFunctionAsmOptimization
{
    protected override AsmFunction? RunOnFunction(AsmFunction input)
    {
        List<AsmNode> nodes = [];
        bool changed = false;

        foreach (AsmNode node in input.Nodes)
        {
            if (node is AsmInstructionNode callInstruction
                && callInstruction.Condition is null
                && callInstruction.FlagEffect == P2FlagEffect.None
                && nodes.Count > 0
                && nodes[^1] is AsmInstructionNode previousInstruction
                && previousInstruction.Mnemonic == P2Mnemonic.MOV
                && previousInstruction.Condition is null
                && previousInstruction.FlagEffect == P2FlagEffect.None
                && previousInstruction.Operands.Count == 2)
            {
                if (callInstruction.Mnemonic == P2Mnemonic.CALL
                    && callInstruction.Operands.Count == 1)
                {
                    P2Mnemonic? specializedCall = GetSpecializedCallMnemonic(previousInstruction.Operands[0]);
                    if (specializedCall is not null)
                    {
                        nodes[^1] = new AsmInstructionNode(
                            specializedCall.Value,
                            [previousInstruction.Operands[1], callInstruction.Operands[0]]);
                        changed = true;
                        continue;
                    }
                }

                if (callInstruction.Mnemonic is P2Mnemonic.CALLPA or P2Mnemonic.CALLPB
                    && callInstruction.Operands.Count == 2
                    && OperandsEquivalent(previousInstruction.Operands[0], callInstruction.Operands[0])
                    && CanElideTransportStore(previousInstruction.Operands[0]))
                {
                    nodes[^1] = new AsmInstructionNode(
                        callInstruction.Mnemonic,
                        [previousInstruction.Operands[1], callInstruction.Operands[1]]);
                    changed = true;
                    continue;
                }
            }

            nodes.Add(node);
        }

        return changed
            ? new AsmFunction(input, nodes)
            : null;
    }

    private static P2Mnemonic? GetSpecializedCallMnemonic(AsmOperand transportDestination)
    {
        if (OperandTargetsSpecialRegister(transportDestination, P2SpecialRegister.PA))
            return P2Mnemonic.CALLPA;

        if (OperandTargetsSpecialRegister(transportDestination, P2SpecialRegister.PB))
            return P2Mnemonic.CALLPB;

        return null;
    }

    private static bool CanElideTransportStore(AsmOperand operand)
    {
        if (operand is AsmSymbolOperand { Symbol: AsmSpillSlotSymbol })
            return true;

        if (operand is AsmSymbolOperand { AddressingMode: AsmSymbolAddressingMode.Register, Symbol: StoragePlace { IsInternalRegisterSlot: true } })
            return true;

        if (operand is AsmSymbolOperand { AddressingMode: AsmSymbolAddressingMode.Register, Symbol: StoragePlace { CanElideTopLevelStoreLoadChains: true } })
            return true;

        return OperandTargetsSpecialRegister(operand, P2SpecialRegister.PA)
            || OperandTargetsSpecialRegister(operand, P2SpecialRegister.PB);
    }

    private static bool OperandTargetsSpecialRegister(AsmOperand operand, P2SpecialRegister register)
    {
        return operand switch
        {
            AsmSymbolOperand symbolOperand when symbolOperand.Symbol is AsmSpecialRegisterSymbol symbol => symbol.Register == new P2Register(register),
            AsmPhysicalRegisterOperand physicalOperand => physicalOperand.Register == new P2Register(register),
            _ => false,
        };
    }
}
