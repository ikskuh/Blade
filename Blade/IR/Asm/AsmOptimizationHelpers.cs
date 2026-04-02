using System.Collections.Generic;
using System.Linq;
using Blade;
using Blade.Semantics;

namespace Blade.IR.Asm;

internal static class AsmOptimizationHelpers
{
    internal static bool IsPlainMov(AsmInstructionNode instruction)
        => instruction.Mnemonic == P2Mnemonic.MOV
            && instruction.Condition is null
            && instruction.FlagEffect == P2FlagEffect.None
            && instruction.Operands.Count == 2;

    internal static bool TryGetTrackedCopy(
        AsmInstructionNode instruction,
        out AsmRegisterOperand destination,
        out AsmOperand source)
    {
        destination = null!;
        source = null!;

        if (!IsPlainMov(instruction)
            || instruction.Operands[0] is not AsmRegisterOperand dest)
        {
            return false;
        }

        if (instruction.Operands[1] is AsmAltPlaceholderOperand)
            return false;

        destination = dest;
        source = instruction.Operands[1];
        return true;
    }

    internal static bool OperandsEquivalent(AsmOperand left, AsmOperand right)
    {
        if (ReferenceEquals(left, right))
            return true;

        return (left, right) switch
        {
            (AsmRegisterOperand lhs, AsmRegisterOperand rhs) => ReferenceEquals(lhs.Register, rhs.Register),
            (AsmImmediateOperand lhs, AsmImmediateOperand rhs) => lhs.Value == rhs.Value,
            (AsmSymbolOperand lhs, AsmSymbolOperand rhs) => SymbolOperandsEquivalent(lhs, rhs),
            (AsmPlaceOperand lhs, AsmPlaceOperand rhs) => ReferenceEquals(lhs.Place, rhs.Place),
            (AsmPhysicalRegisterOperand lhs, AsmSymbolOperand rhs) => PhysicalAndSymbolEquivalent(lhs, rhs),
            (AsmSymbolOperand lhs, AsmPhysicalRegisterOperand rhs) => PhysicalAndSymbolEquivalent(rhs, lhs),
            (AsmPhysicalRegisterOperand lhs, AsmPhysicalRegisterOperand rhs) => lhs.Register == rhs.Register,
            (AsmAltPlaceholderOperand lhs, AsmAltPlaceholderOperand rhs) => lhs.Kind == rhs.Kind,
            _ => false,
        };
    }

    private static bool SymbolOperandsEquivalent(AsmSymbolOperand left, AsmSymbolOperand right)
    {
        if (left.AddressingMode != right.AddressingMode)
            return false;

        if (ReferenceEquals(left.Symbol, right.Symbol))
            return true;

        if (left.Symbol is AsmSpecialRegisterSymbol leftRegister
            && right.Symbol is AsmSpecialRegisterSymbol rightRegister)
        {
            return leftRegister.Register == rightRegister.Register;
        }

        return false;
    }

    private static bool PhysicalAndSymbolEquivalent(AsmPhysicalRegisterOperand physical, AsmSymbolOperand symbol)
    {
        return symbol.AddressingMode == AsmSymbolAddressingMode.Register
            && symbol.Symbol is AsmSpecialRegisterSymbol specialRegister
            && specialRegister.Register == physical.Register;
    }

    internal static HashSet<ControlFlowLabelSymbol> CollectJumpTargets(IReadOnlyList<AsmNode> nodes)
    {
        HashSet<ControlFlowLabelSymbol> targets = [];
        foreach (AsmNode node in nodes)
        {
            if (node is AsmInstructionNode instruction
                && instruction.Mnemonic == P2Mnemonic.JMP
                && instruction.Operands.Count == 1
                && instruction.Operands[0] is AsmSymbolOperand { Symbol: ControlFlowLabelSymbol symbol })
            {
                targets.Add(symbol);
            }
        }

        return targets;
    }

    internal static bool HasImmediateSymbolOperand(AsmInstructionNode instruction)
    {
        for (int operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
        {
            if (instruction.Operands[operandIndex] is not AsmSymbolOperand)
                continue;

            if (P2InstructionMetadata.UsesImmediateSymbolSyntax(instruction.Mnemonic, instruction.Operands.Count, operandIndex))
                return true;
        }

        return false;
    }

    internal static bool IsBarrier(AsmInstructionNode instruction)
    {
        if (instruction.Condition is not null || instruction.FlagEffect != P2FlagEffect.None)
            return true;

        return P2InstructionMetadata.IsControlFlow(instruction.Mnemonic, instruction.Operands.Count)
            || HasImmediateSymbolOperand(instruction);
    }

    internal static bool IsPureRegisterLocalInstruction(AsmInstructionNode instruction)
    {
        if (instruction.Condition is not null
            || instruction.FlagEffect != P2FlagEffect.None
            || instruction.Operands.Count == 0
            || instruction.Operands[0] is not AsmRegisterOperand
            || !P2InstructionMetadata.IsPureRegisterLocal(instruction.Mnemonic, instruction.Operands.Count))
        {
            return false;
        }

        for (int i = 1; i < instruction.Operands.Count; i++)
        {
            if (instruction.Operands[i] is AsmRegisterOperand or AsmImmediateOperand)
                continue;
            return false;
        }

        return true;
    }

    internal static IEnumerable<VirtualAsmRegister> EnumerateUsedRegisters(AsmInstructionNode instruction)
    {
        int startIndex = IsPlainMov(instruction) ? 1 : 0;
        for (int i = startIndex; i < instruction.Operands.Count; i++)
        {
            if (instruction.Operands[i] is AsmRegisterOperand register)
                yield return register.Register;
        }
    }

    internal static bool TryGetDefinedRegister(AsmInstructionNode instruction, out VirtualAsmRegister? register)
    {
        register = null;
        if (IsBarrier(instruction))
            return false;

        if (instruction.Operands.Count == 0 || instruction.Operands[0] is not AsmRegisterOperand destination)
            return false;

        register = destination.Register;
        return true;
    }

    internal static AsmOperand ResolveAlias(AsmOperand operand, IReadOnlyDictionary<VirtualAsmRegister, AsmOperand> aliases)
    {
        AsmOperand current = operand;
        HashSet<VirtualAsmRegister> seen = [];
        while (current is AsmRegisterOperand register
            && aliases.TryGetValue(register.Register, out AsmOperand? next)
            && seen.Add(register.Register))
        {
            current = next;
        }

        return current;
    }

    internal static void InvalidateAliases(
        AsmInstructionNode instruction,
        IDictionary<VirtualAsmRegister, AsmOperand> aliases)
    {
        if (instruction.Operands.Count == 0)
            return;

        AsmOperand written = instruction.Operands[0];
        if (written is AsmRegisterOperand register)
            aliases.Remove(register.Register);

        foreach (VirtualAsmRegister alias in aliases.Keys.ToList())
        {
            if (OperandsEquivalent(aliases[alias], written))
                aliases.Remove(alias);
        }
    }

    internal static AsmInstructionNode RewriteInstructionSources(
        AsmInstructionNode instruction,
        IReadOnlyDictionary<VirtualAsmRegister, AsmOperand> aliases)
    {
        if (instruction.Operands.Count <= 1)
            return instruction;

        List<AsmOperand> operands = new(instruction.Operands.Count) { instruction.Operands[0] };
        bool changed = false;
        for (int i = 1; i < instruction.Operands.Count; i++)
        {
            AsmOperand rewritten = ResolveAlias(instruction.Operands[i], aliases);
            operands.Add(rewritten);
            changed |= !ReferenceEquals(rewritten, instruction.Operands[i]);
        }

        return changed
            ? new AsmInstructionNode(
                instruction.Mnemonic,
                operands,
                instruction.Condition,
                instruction.FlagEffect,
                instruction.IsNonElidable,
                instruction.IsPhiMove)
            : instruction;
    }

    internal static bool TryGetNextLabel(IReadOnlyList<AsmNode> nodes, int startIndex, out ControlFlowLabelSymbol? label)
    {
        for (int i = startIndex; i < nodes.Count; i++)
        {
            switch (nodes[i])
            {
                case AsmCommentNode:
                    continue;

                case AsmLabelNode nextLabel:
                    label = nextLabel.Label;
                    return true;

                default:
                    label = null;
                    return false;
            }
        }

        label = null;
        return false;
    }

    internal static bool IsControlFlowInstruction(AsmInstructionNode instruction)
        => P2InstructionMetadata.IsControlFlow(instruction.Mnemonic, instruction.Operands.Count);

    internal static bool EndsWithTargetedLabel(IReadOnlyList<AsmNode> nodes, IReadOnlySet<ControlFlowLabelSymbol> targets)
        => nodes.Count > 0 && nodes[^1] is AsmLabelNode label && targets.Contains(label.Label);

    internal static bool IsDeadInstruction(AsmInstructionNode instruction, IReadOnlySet<VirtualAsmRegister> live)
    {
        if (instruction.IsNonElidable)
            return false;

        if (TryGetTrackedCopy(instruction, out AsmRegisterOperand copyDestination, out _))
            return !live.Contains(copyDestination.Register);

        if (!IsPureRegisterLocalInstruction(instruction)
            || instruction.Operands[0] is not AsmRegisterOperand destination)
        {
            return false;
        }

        return !live.Contains(destination.Register);
    }

    internal static bool UsesNonLinearControlFlow(AsmFunction function)
    {
        for (int i = 0; i < function.Nodes.Count; i++)
        {
            if (function.Nodes[i] is not AsmInstructionNode instruction)
                continue;

            if (!P2InstructionMetadata.TryGetInstructionForm(instruction.Mnemonic, instruction.Operands.Count, out P2InstructionFormInfo form)
                || !form.IsBranch
                || form.IsReturn)
            {
                continue;
            }

            bool isLinearJump =
                instruction.Mnemonic == P2Mnemonic.JMP
                && instruction.Condition is null
                && instruction.Operands.Count == 1
                && instruction.Operands[0] is AsmSymbolOperand target
                && target.Symbol is ControlFlowLabelSymbol targetLabel
                && TryGetNextLabel(function.Nodes, i + 1, out ControlFlowLabelSymbol? nextLabel)
                && ReferenceEquals(nextLabel, targetLabel);

            if (!isLinearJump)
            {
                return true;
            }
        }

        return false;
    }

    internal static P2ConditionCode InvertPredicate(P2ConditionCode predicate)
        => predicate switch
        {
            P2ConditionCode.IF_Z => P2ConditionCode.IF_NZ,
            P2ConditionCode.IF_NZ => P2ConditionCode.IF_Z,
            P2ConditionCode.IF_C => P2ConditionCode.IF_NC,
            P2ConditionCode.IF_NC => P2ConditionCode.IF_C,
            _ => predicate,
        };

    internal static bool TryFuseMuxPair(AsmInstructionNode first, AsmInstructionNode second, out AsmInstructionNode? fused)
    {
        fused = null;
        if (first.IsNonElidable
            || second.IsNonElidable
            || first.FlagEffect != P2FlagEffect.None
            || second.FlagEffect != P2FlagEffect.None)
        {
            return false;
        }

        if (first.Operands.Count != 2 || second.Operands.Count != 2)
            return false;

        if (!OperandsEquivalent(first.Operands[0], second.Operands[0])
            || !OperandsEquivalent(first.Operands[1], second.Operands[1]))
        {
            return false;
        }

        if (first.Condition == P2ConditionCode.IF_C && second.Condition == P2ConditionCode.IF_NC
            && first.Mnemonic == P2Mnemonic.OR && second.Mnemonic == P2Mnemonic.ANDN)
        {
            fused = new AsmInstructionNode(P2Mnemonic.MUXC, [first.Operands[0], first.Operands[1]]);
        }
        else if (first.Condition == P2ConditionCode.IF_NC && second.Condition == P2ConditionCode.IF_C
                 && first.Mnemonic == P2Mnemonic.ANDN && second.Mnemonic == P2Mnemonic.OR)
        {
            fused = new AsmInstructionNode(P2Mnemonic.MUXC, [first.Operands[0], first.Operands[1]]);
        }
        else if (first.Condition == P2ConditionCode.IF_NC && second.Condition == P2ConditionCode.IF_C
                 && first.Mnemonic == P2Mnemonic.OR && second.Mnemonic == P2Mnemonic.ANDN)
        {
            fused = new AsmInstructionNode(P2Mnemonic.MUXNC, [first.Operands[0], first.Operands[1]]);
        }
        else if (first.Condition == P2ConditionCode.IF_C && second.Condition == P2ConditionCode.IF_NC
                 && first.Mnemonic == P2Mnemonic.ANDN && second.Mnemonic == P2Mnemonic.OR)
        {
            fused = new AsmInstructionNode(P2Mnemonic.MUXNC, [first.Operands[0], first.Operands[1]]);
        }

        return fused is not null;
    }

    internal static bool IsSemanticNop(AsmInstructionNode instruction)
    {
        if (instruction.IsNonElidable)
            return false;

        if (instruction.Condition is not null || instruction.FlagEffect != P2FlagEffect.None)
            return false;

        if (instruction.Mnemonic == P2Mnemonic.NOP)
            return true;

        if (instruction.Operands.Count != 2)
            return false;

        AsmOperand left = instruction.Operands[0];
        AsmOperand right = instruction.Operands[1];

        if (instruction.Mnemonic == P2Mnemonic.MOV && OperandsEquivalent(left, right))
            return true;

        if (right is not AsmImmediateOperand immediate)
            return false;

        return instruction.Mnemonic switch
        {
            P2Mnemonic.OR when immediate.Value == 0 => true,
            P2Mnemonic.XOR when immediate.Value == 0 => true,
            P2Mnemonic.ADD when immediate.Value == 0 => true,
            P2Mnemonic.SUB when immediate.Value == 0 => true,
            P2Mnemonic.SHL when immediate.Value == 0 => true,
            P2Mnemonic.SHR when immediate.Value == 0 => true,
            P2Mnemonic.SAR when immediate.Value == 0 => true,
            P2Mnemonic.ROL when immediate.Value == 0 => true,
            P2Mnemonic.ROR when immediate.Value == 0 => true,
            _ => false,
        };
    }
}
