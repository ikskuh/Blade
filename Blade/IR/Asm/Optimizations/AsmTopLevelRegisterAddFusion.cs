using System.Collections.Generic;
using Blade.IR;
using static Blade.IR.Asm.AsmOptimizationHelpers;

namespace Blade.IR.Asm.Optimizations;

[AsmOptimization("top-level-reg-add-fusion", Priority = 850, State = AsmOptimizationState.PreRegAlloc | AsmOptimizationState.PostRegAlloc)]
public sealed class AsmTopLevelRegisterAddFusion : PerFunctionAsmOptimization
{
    protected override AsmFunction? RunOnFunction(AsmFunction input)
    {
        List<AsmNode> nodes = [];
        bool changed = false;

        for (int i = 0; i < input.Nodes.Count; i++)
        {
            if (i + 2 < input.Nodes.Count
                && input.Nodes[i] is AsmInstructionNode load
                && input.Nodes[i + 1] is AsmInstructionNode update
                && input.Nodes[i + 2] is AsmInstructionNode store
                && TryFuseTopLevelRegisterAdd(load, update, store, out AsmInstructionNode? fused))
            {
                nodes.Add(fused!);
                i += 2;
                changed = true;
                continue;
            }

            nodes.Add(input.Nodes[i]);
        }

        return changed
            ? new AsmFunction(input, nodes)
            : null;
    }

    private static bool TryFuseTopLevelRegisterAdd(
        AsmInstructionNode load,
        AsmInstructionNode update,
        AsmInstructionNode store,
        out AsmInstructionNode? fused)
    {
        fused = null;

        if (!IsPlainMov(load)
            || !IsPlainMov(store)
            || load.IsNonElidable
            || update.IsNonElidable
            || store.IsNonElidable
            || update.Condition is not null
            || update.FlagEffect != P2FlagEffect.None
            || update.Mnemonic != P2Mnemonic.ADD
            || update.Operands.Count != 2)
        {
            return false;
        }

        AsmOperand place = load.Operands[1];
        if (!CanElideTopLevelRegisterOperand(place)
            || !OperandsEquivalent(place, store.Operands[0])
            || !IsRegisterLikeOperand(load.Operands[0])
            || !OperandsEquivalent(load.Operands[0], update.Operands[0])
            || !OperandsEquivalent(load.Operands[0], store.Operands[1]))
        {
            return false;
        }

        fused = new AsmInstructionNode(P2Mnemonic.ADD, [place, update.Operands[1]]);
        return true;
    }

    private static bool CanElideTopLevelRegisterOperand(AsmOperand operand)
    {
        return operand switch
        {
            AsmSymbolOperand { AddressingMode: AsmSymbolAddressingMode.Register, Symbol: StoragePlace { CanElideTopLevelStoreLoadChains: true } } => true,
            _ => false,
        };
    }

    private static bool IsRegisterLikeOperand(AsmOperand operand)
        => operand is AsmRegisterOperand or AsmPhysicalRegisterOperand;
}
