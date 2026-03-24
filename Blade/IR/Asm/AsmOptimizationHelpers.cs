using System.Collections.Generic;
using System.Linq;

namespace Blade.IR.Asm;

internal static class AsmOptimizationHelpers
{
    internal static bool IsPlainMov(AsmInstructionNode instruction)
        => instruction.Opcode == "MOV"
            && instruction.Predicate is null
            && instruction.FlagEffect == AsmFlagEffect.None
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
            (AsmRegisterOperand lhs, AsmRegisterOperand rhs) => lhs.RegisterId == rhs.RegisterId,
            (AsmImmediateOperand lhs, AsmImmediateOperand rhs) => lhs.Value == rhs.Value,
            (AsmSymbolOperand lhs, AsmSymbolOperand rhs) => lhs.Name == rhs.Name,
            (AsmPlaceOperand lhs, AsmPlaceOperand rhs) => lhs.Place.Symbol.Id == rhs.Place.Symbol.Id,
            (AsmPhysicalRegisterOperand lhs, AsmPhysicalRegisterOperand rhs) => lhs.Address == rhs.Address,
            _ => false,
        };
    }

    internal static HashSet<string> CollectJumpTargets(IReadOnlyList<AsmNode> nodes)
    {
        HashSet<string> targets = [];
        foreach (AsmNode node in nodes)
        {
            if (node is AsmInstructionNode instruction
                && instruction.Opcode == "JMP"
                && instruction.Operands.Count == 1
                && instruction.Operands[0] is AsmSymbolOperand symbol)
            {
                targets.Add(symbol.Name);
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

            if (P2InstructionMetadata.UsesImmediateSymbolSyntax(instruction.Opcode, instruction.Operands.Count, operandIndex))
                return true;
        }

        return false;
    }

    internal static bool IsBarrier(AsmInstructionNode instruction)
    {
        if (instruction.Predicate is not null || instruction.FlagEffect != AsmFlagEffect.None)
            return true;

        return P2InstructionMetadata.IsControlFlow(instruction.Opcode, instruction.Operands.Count)
            || HasImmediateSymbolOperand(instruction);
    }

    internal static bool IsPureRegisterLocalInstruction(AsmInstructionNode instruction)
    {
        if (instruction.Predicate is not null
            || instruction.FlagEffect != AsmFlagEffect.None
            || instruction.Operands.Count == 0
            || instruction.Operands[0] is not AsmRegisterOperand
            || !P2InstructionMetadata.IsPureRegisterLocal(instruction.Opcode, instruction.Operands.Count))
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

    internal static IEnumerable<int> EnumerateUsedRegisters(AsmInstructionNode instruction)
    {
        int startIndex = IsPlainMov(instruction) ? 1 : 0;
        for (int i = startIndex; i < instruction.Operands.Count; i++)
        {
            if (instruction.Operands[i] is AsmRegisterOperand register)
                yield return register.RegisterId;
        }
    }

    internal static bool TryGetDefinedRegister(AsmInstructionNode instruction, out int registerId)
    {
        registerId = default;
        if (IsBarrier(instruction))
            return false;

        if (instruction.Operands.Count == 0 || instruction.Operands[0] is not AsmRegisterOperand register)
            return false;

        registerId = register.RegisterId;
        return true;
    }

    internal static AsmOperand ResolveAlias(AsmOperand operand, IReadOnlyDictionary<int, AsmOperand> aliases)
    {
        AsmOperand current = operand;
        HashSet<int> seen = [];
        while (current is AsmRegisterOperand register
            && aliases.TryGetValue(register.RegisterId, out AsmOperand? next)
            && seen.Add(register.RegisterId))
        {
            current = next;
        }

        return current;
    }

    internal static void InvalidateAliases(
        AsmInstructionNode instruction,
        IDictionary<int, AsmOperand> aliases)
    {
        if (instruction.Operands.Count == 0)
            return;

        AsmOperand written = instruction.Operands[0];
        if (written is AsmRegisterOperand register)
            aliases.Remove(register.RegisterId);

        foreach (int alias in aliases.Keys.ToList())
        {
            if (OperandsEquivalent(aliases[alias], written))
                aliases.Remove(alias);
        }
    }

    internal static AsmInstructionNode RewriteInstructionSources(
        AsmInstructionNode instruction,
        IReadOnlyDictionary<int, AsmOperand> aliases)
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
                instruction.Opcode,
                operands,
                instruction.Predicate,
                instruction.FlagEffect,
                instruction.IsNonElidable)
            : instruction;
    }

    internal static AsmImplicitUseNode RewriteImplicitUse(
        AsmImplicitUseNode implicitUse,
        IReadOnlyDictionary<int, AsmOperand> aliases)
    {
        List<AsmOperand> operands = new(implicitUse.Operands.Count);
        bool changed = false;
        foreach (AsmOperand operand in implicitUse.Operands)
        {
            AsmOperand rewritten = ResolveAlias(operand, aliases);
            operands.Add(rewritten);
            changed |= !ReferenceEquals(rewritten, operand);
        }

        return changed ? new AsmImplicitUseNode(operands) : implicitUse;
    }

    internal static bool TryGetNextLabel(IReadOnlyList<AsmNode> nodes, int startIndex, out string? label)
    {
        for (int i = startIndex; i < nodes.Count; i++)
        {
            switch (nodes[i])
            {
                case AsmCommentNode:
                    continue;

                case AsmLabelNode nextLabel:
                    label = nextLabel.Name;
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
        => P2InstructionMetadata.IsControlFlow(instruction.Opcode, instruction.Operands.Count);

    internal static bool EndsWithTargetedLabel(IReadOnlyList<AsmNode> nodes, IReadOnlySet<string> targets)
        => nodes.Count > 0 && nodes[^1] is AsmLabelNode label && targets.Contains(label.Name);

    internal static bool IsDeadInstruction(AsmInstructionNode instruction, IReadOnlySet<int> live)
    {
        if (instruction.IsNonElidable)
            return false;

        if (TryGetTrackedCopy(instruction, out AsmRegisterOperand copyDestination, out _))
            return !live.Contains(copyDestination.RegisterId);

        if (!IsPureRegisterLocalInstruction(instruction)
            || instruction.Operands[0] is not AsmRegisterOperand destination)
        {
            return false;
        }

        return !live.Contains(destination.RegisterId);
    }

    internal static bool UsesNonLinearControlFlow(AsmFunction function)
    {
        for (int i = 0; i < function.Nodes.Count; i++)
        {
            if (function.Nodes[i] is not AsmInstructionNode instruction)
                continue;

            if (!P2InstructionMetadata.TryGetInstructionForm(instruction.Opcode, instruction.Operands.Count, out P2InstructionFormInfo form)
                || !form.IsBranch
                || form.IsReturn)
            {
                continue;
            }

            bool isLinearJump =
                instruction.Opcode == "JMP"
                && instruction.Predicate is null
                && instruction.Operands.Count == 1
                && instruction.Operands[0] is AsmSymbolOperand target
                && TryGetNextLabel(function.Nodes, i + 1, out string? nextLabel)
                && nextLabel == target.Name;

            if (!isLinearJump)
            {
                return true;
            }
        }

        return false;
    }

    internal static string InvertPredicate(string predicate)
        => predicate switch
        {
            "IF_Z" => "IF_NZ",
            "IF_NZ" => "IF_Z",
            "IF_C" => "IF_NC",
            "IF_NC" => "IF_C",
            _ => predicate,
        };

    internal static bool TryFuseMuxPair(AsmInstructionNode first, AsmInstructionNode second, out AsmInstructionNode? fused)
    {
        fused = null;
        if (first.IsNonElidable
            || second.IsNonElidable
            || first.FlagEffect != AsmFlagEffect.None
            || second.FlagEffect != AsmFlagEffect.None)
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

        if (first.Predicate == "IF_C" && second.Predicate == "IF_NC" && first.Opcode == "OR" && second.Opcode == "ANDN")
            fused = new AsmInstructionNode("MUXC", [first.Operands[0], first.Operands[1]]);
        else if (first.Predicate == "IF_NC" && second.Predicate == "IF_C" && first.Opcode == "ANDN" && second.Opcode == "OR")
            fused = new AsmInstructionNode("MUXC", [first.Operands[0], first.Operands[1]]);
        else if (first.Predicate == "IF_NC" && second.Predicate == "IF_C" && first.Opcode == "OR" && second.Opcode == "ANDN")
            fused = new AsmInstructionNode("MUXNC", [first.Operands[0], first.Operands[1]]);
        else if (first.Predicate == "IF_C" && second.Predicate == "IF_NC" && first.Opcode == "ANDN" && second.Opcode == "OR")
            fused = new AsmInstructionNode("MUXNC", [first.Operands[0], first.Operands[1]]);

        return fused is not null;
    }

    internal static bool IsSemanticNop(AsmInstructionNode instruction)
    {
        if (instruction.IsNonElidable)
            return false;

        if (instruction.Predicate is not null || instruction.FlagEffect != AsmFlagEffect.None)
            return false;

        if (instruction.Opcode == "NOP")
            return true;

        if (instruction.Operands.Count != 2)
            return false;

        AsmOperand left = instruction.Operands[0];
        AsmOperand right = instruction.Operands[1];

        if (instruction.Opcode == "MOV" && OperandsEquivalent(left, right))
            return true;

        if (right is not AsmImmediateOperand immediate)
            return false;

        return instruction.Opcode switch
        {
            "OR" when immediate.Value == 0 => true,
            "XOR" when immediate.Value == 0 => true,
            "ADD" when immediate.Value == 0 => true,
            "SUB" when immediate.Value == 0 => true,
            "SHL" when immediate.Value == 0 => true,
            "SHR" when immediate.Value == 0 => true,
            "SAR" when immediate.Value == 0 => true,
            "ROL" when immediate.Value == 0 => true,
            "ROR" when immediate.Value == 0 => true,
            _ => false,
        };
    }
}
