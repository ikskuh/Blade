using System.Collections.Generic;
using System.Linq;

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
        AsmFunction current = function;
        bool changed;
        do
        {
            changed = false;
            (current, bool rewriteChanged) = RewriteStraightLineCopyUses(current);
            changed |= rewriteChanged;
            (current, bool deadMoveChanged) = RemoveDeadRegisterMoves(current);
            changed |= deadMoveChanged;
            (current, bool jumpsChanged) = RemoveJumpsToNextLabel(current);
            changed |= jumpsChanged;
            (current, bool cleanupChanged) = CleanupLabelsAndSelfMoves(current);
            changed |= cleanupChanged;
        }
        while (changed);

        return current;
    }

    private static (AsmFunction Function, bool Changed) CleanupLabelsAndSelfMoves(AsmFunction function)
    {
        List<AsmNode> nodes = [];
        AsmLabelNode? previousLabel = null;
        bool changed = false;

        foreach (AsmNode node in function.Nodes)
        {
            if (node is AsmLabelNode label)
            {
                if (previousLabel is not null && previousLabel.Name == label.Name)
                {
                    changed = true;
                    continue;
                }

                previousLabel = label;
                nodes.Add(label);
                continue;
            }

            previousLabel = null;

            if (node is AsmInstructionNode instruction
                && IsPlainMov(instruction)
                && OperandsEquivalent(instruction.Operands[0], instruction.Operands[1]))
            {
                changed = true;
                continue;
            }

            nodes.Add(node);
        }

        return (new AsmFunction(function.Name, function.IsEntryPoint, function.CcTier, nodes), changed);
    }

    private static (AsmFunction Function, bool Changed) RewriteStraightLineCopyUses(AsmFunction function)
    {
        Dictionary<int, AsmOperand> aliases = [];
        List<AsmNode> nodes = [];
        bool changed = false;

        foreach (AsmNode node in function.Nodes)
        {
            switch (node)
            {
                case AsmCommentNode:
                    nodes.Add(node);
                    break;

                case AsmLabelNode:
                case AsmDirectiveNode:
                case AsmInlineTextNode:
                    aliases.Clear();
                    nodes.Add(node);
                    break;

                case AsmInstructionNode instruction when IsBarrier(instruction):
                    aliases.Clear();
                    nodes.Add(instruction);
                    break;

                case AsmInstructionNode instruction:
                {
                    AsmInstructionNode rewritten = RewriteInstructionSources(instruction, aliases);
                    changed |= !ReferenceEquals(rewritten, instruction);

                    InvalidateAliases(rewritten, aliases);
                    if (TryGetTrackedCopy(rewritten, out AsmRegisterOperand dest, out AsmOperand source)
                        && !OperandsEquivalent(dest, source))
                    {
                        aliases[dest.RegisterId] = ResolveAlias(source, aliases);
                    }
                    nodes.Add(rewritten);
                    break;
                }

                default:
                    nodes.Add(node);
                    break;
            }
        }

        return (new AsmFunction(function.Name, function.IsEntryPoint, function.CcTier, nodes), changed);
    }

    private static (AsmFunction Function, bool Changed) RemoveDeadRegisterMoves(AsmFunction function)
    {
        HashSet<int> live = [];
        List<AsmNode> kept = [];
        bool changed = false;

        for (int i = function.Nodes.Count - 1; i >= 0; i--)
        {
            AsmNode node = function.Nodes[i];
            if (node is AsmInstructionNode instruction)
            {
                if (TryGetTrackedCopy(instruction, out AsmRegisterOperand dest, out _)
                    && !live.Contains(dest.RegisterId))
                {
                    changed = true;
                    continue;
                }

                if (TryGetDefinedRegister(instruction, out int definedRegister))
                    live.Remove(definedRegister);

                foreach (int usedRegister in EnumerateUsedRegisters(instruction))
                    live.Add(usedRegister);
            }

            kept.Add(node);
        }

        kept.Reverse();
        return (new AsmFunction(function.Name, function.IsEntryPoint, function.CcTier, kept), changed);
    }

    private static (AsmFunction Function, bool Changed) RemoveJumpsToNextLabel(AsmFunction function)
    {
        List<AsmNode> nodes = [];
        bool changed = false;

        for (int i = 0; i < function.Nodes.Count; i++)
        {
            AsmNode node = function.Nodes[i];
            if (node is AsmInstructionNode instruction
                && instruction.Opcode == "JMP"
                && instruction.Predicate is null
                && instruction.Operands.Count == 1
                && instruction.Operands[0] is AsmSymbolOperand target
                && TryGetNextLabel(function.Nodes, i + 1, out string? nextLabel)
                && nextLabel == target.Name)
            {
                changed = true;
                continue;
            }

            nodes.Add(node);
        }

        return (new AsmFunction(function.Name, function.IsEntryPoint, function.CcTier, nodes), changed);
    }

    private static bool TryGetNextLabel(IReadOnlyList<AsmNode> nodes, int startIndex, out string? label)
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

    private static bool IsPlainMov(AsmInstructionNode instruction)
        => instruction.Opcode == "MOV"
            && instruction.Predicate is null
            && instruction.FlagEffect == AsmFlagEffect.None
            && instruction.Operands.Count == 2;

    private static bool TryGetTrackedCopy(
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

    private static AsmInstructionNode RewriteInstructionSources(
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
            ? new AsmInstructionNode(instruction.Opcode, operands, instruction.Predicate, instruction.FlagEffect)
            : instruction;
    }

    private static AsmOperand ResolveAlias(AsmOperand operand, IReadOnlyDictionary<int, AsmOperand> aliases)
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

    private static void InvalidateAliases(
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

    private static IEnumerable<int> EnumerateUsedRegisters(AsmInstructionNode instruction)
    {
        int startIndex = IsPlainMov(instruction) ? 1 : 0;
        for (int i = startIndex; i < instruction.Operands.Count; i++)
        {
            if (instruction.Operands[i] is AsmRegisterOperand register)
                yield return register.RegisterId;
        }
    }

    private static bool TryGetDefinedRegister(AsmInstructionNode instruction, out int registerId)
    {
        registerId = default;
        if (IsBarrier(instruction))
            return false;

        if (instruction.Operands.Count == 0 || instruction.Operands[0] is not AsmRegisterOperand register)
            return false;

        registerId = register.RegisterId;
        return true;
    }

    private static bool IsBarrier(AsmInstructionNode instruction)
    {
        if (instruction.Predicate is not null || instruction.FlagEffect != AsmFlagEffect.None)
            return true;

        return instruction.Opcode is "JMP" or "TJZ" or "TJNZ" or "TJF" or "TJNF"
            or "DJNZ" or "DJZ"
            or "CALL" or "CALLA" or "CALLB" or "CALLD" or "CALLPA" or "CALLPB"
            or "RET" or "RETA" or "RETB" or "RETI0" or "RETI1" or "RETI2" or "RETI3"
            or "LOC";
    }

    private static bool OperandsEquivalent(AsmOperand left, AsmOperand right)
    {
        if (ReferenceEquals(left, right))
            return true;

        return (left, right) switch
        {
            (AsmRegisterOperand lhs, AsmRegisterOperand rhs) => lhs.RegisterId == rhs.RegisterId,
            (AsmImmediateOperand lhs, AsmImmediateOperand rhs) => lhs.Value == rhs.Value,
            (AsmSymbolOperand lhs, AsmSymbolOperand rhs) => lhs.Name == rhs.Name,
            (AsmPhysicalRegisterOperand lhs, AsmPhysicalRegisterOperand rhs) => lhs.Address == rhs.Address,
            _ => false,
        };
    }
}
