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
        return new AsmModule(module.StoragePlaces, functions);
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
            (current, bool deadInstructionChanged) = RemoveDeadPureRegisterInstructions(current);
            changed |= deadInstructionChanged;
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

                case AsmImplicitUseNode implicitUse:
                    nodes.Add(RewriteImplicitUse(implicitUse, aliases));
                    aliases.Clear();
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

    private static (AsmFunction Function, bool Changed) RemoveDeadPureRegisterInstructions(AsmFunction function)
    {
        HashSet<int> live = [];
        List<AsmNode> kept = [];
        bool changed = false;

        for (int i = function.Nodes.Count - 1; i >= 0; i--)
        {
            AsmNode node = function.Nodes[i];
            if (node is AsmInstructionNode instruction)
            {
                if (IsDeadInstruction(instruction, live))
                {
                    changed = true;
                    continue;
                }

                if (TryGetDefinedRegister(instruction, out int definedRegister))
                    live.Remove(definedRegister);

                foreach (int usedRegister in EnumerateUsedRegisters(instruction))
                    live.Add(usedRegister);
            }
            else if (node is AsmImplicitUseNode implicitUse)
            {
                foreach (AsmOperand operand in implicitUse.Operands)
                {
                    if (operand is AsmRegisterOperand reg)
                        live.Add(reg.RegisterId);
                }
            }
            else if (node is AsmInlineTextNode inlineText)
            {
                foreach (AsmOperand operand in inlineText.Bindings.Values)
                {
                    if (operand is AsmRegisterOperand reg)
                        live.Add(reg.RegisterId);
                }
            }

            kept.Add(node);
        }

        kept.Reverse();
        return (new AsmFunction(function.Name, function.IsEntryPoint, function.CcTier, kept), changed);
    }

    private static bool IsDeadInstruction(AsmInstructionNode instruction, IReadOnlySet<int> live)
    {
        if (TryGetTrackedCopy(instruction, out AsmRegisterOperand copyDestination, out _))
            return !live.Contains(copyDestination.RegisterId);

        if (!P2OpcodeInfo.IsPureRegisterLocalInstruction(instruction)
            || instruction.Operands[0] is not AsmRegisterOperand destination)
        {
            return false;
        }

        return !live.Contains(destination.RegisterId);
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

    private static AsmImplicitUseNode RewriteImplicitUse(
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
            (AsmPlaceOperand lhs, AsmPlaceOperand rhs) => lhs.Place.Symbol.Id == rhs.Place.Symbol.Id,
            (AsmPhysicalRegisterOperand lhs, AsmPhysicalRegisterOperand rhs) => lhs.Address == rhs.Address,
            _ => false,
        };
    }
}
