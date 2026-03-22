using System;
using System.Collections.Generic;
using System.Linq;
using Blade;

namespace Blade.IR.Asm;

public static class AsmOptimizer
{
    public static AsmModule Optimize(AsmModule module, IReadOnlyList<string> enabledOptimizations)
    {
        Requires.NotNull(module);
        Requires.NotNull(enabledOptimizations);

        List<AsmFunction> functions = new(module.Functions.Count);
        foreach (AsmFunction function in module.Functions)
            functions.Add(OptimizeFunction(function, enabledOptimizations));
        return new AsmModule(module.StoragePlaces, functions);
    }

    private static AsmFunction OptimizeFunction(AsmFunction function, IReadOnlyList<string> enabledOptimizations)
    {
        AsmFunction current = function;
        bool changed;
        do
        {
            changed = false;
            foreach (string optimization in enabledOptimizations)
            {
                (current, bool stageChanged) = optimization switch
                {
                    "copy-prop" => RewriteStraightLineCopyUses(current),
                    "dce-reg" => RemoveDeadPureRegisterInstructions(current),
                    "drop-jmp-next" => RemoveJumpsToNextLabel(current),
                    "ret-fusion" => FuseRetIntoPreviousInstruction(current),
                    "conditional-move-fusion" => FuseConditionalJumpOverSingleInstruction(current),
                    "muxc-fusion" => FuseMuxConditionPair(current),
                    "elide-nops" => ElideSemanticNops(current),
                    "cleanup-self-mov" => CleanupLabelsAndSelfMoves(current),
                    _ => (current, false),
                };
                changed |= stageChanged;
            }
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
        if (!UsesNonLinearControlFlow(function))
            return RemoveDeadPureRegisterInstructionsStraightLine(function);

        FunctionLiveness liveness = LivenessAnalyzer.Analyze(function);
        List<AsmNode> kept = [];
        bool changed = false;

        for (int i = function.Nodes.Count - 1; i >= 0; i--)
        {
            AsmNode node = function.Nodes[i];
            if (node is AsmInstructionNode instruction)
            {
                IReadOnlySet<int> liveAfterInstruction = liveness.LiveRegistersAfterInstruction.TryGetValue(i, out HashSet<int>? liveSet)
                    ? liveSet
                    : [];

                if (IsDeadInstruction(instruction, liveAfterInstruction))
                {
                    changed = true;
                    continue;
                }
            }

            kept.Add(node);
        }

        kept.Reverse();
        return (new AsmFunction(function.Name, function.IsEntryPoint, function.CcTier, kept), changed);
    }

    private static (AsmFunction Function, bool Changed) RemoveDeadPureRegisterInstructionsStraightLine(AsmFunction function)
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

    private static bool UsesNonLinearControlFlow(AsmFunction function)
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

    private static bool IsDeadInstruction(AsmInstructionNode instruction, IReadOnlySet<int> live)
    {
        if (TryGetTrackedCopy(instruction, out AsmRegisterOperand copyDestination, out _))
            return !live.Contains(copyDestination.RegisterId);

        if (!IsPureRegisterLocalInstruction(instruction)
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


    private static (AsmFunction Function, bool Changed) FuseRetIntoPreviousInstruction(AsmFunction function)
    {
        HashSet<string> targetedLabels = CollectJumpTargets(function.Nodes);
        List<AsmNode> nodes = [];
        bool changed = false;

        for (int i = 0; i < function.Nodes.Count; i++)
        {
            AsmNode node = function.Nodes[i];
            if (node is AsmInstructionNode instruction
                && instruction.Opcode == "RET"
                && instruction.Predicate is null
                && instruction.Operands.Count == 0
                && instruction.FlagEffect == AsmFlagEffect.None
                && i > 0
                && nodes.Count > 0
                && nodes[^1] is AsmInstructionNode previous
                && previous.Predicate is null
                && !IsControlFlowInstruction(previous)
                && !EndsWithTargetedLabel(nodes, targetedLabels))
            {
                nodes[^1] = new AsmInstructionNode(previous.Opcode, previous.Operands, "_RET_", previous.FlagEffect);
                changed = true;
                continue;
            }

            nodes.Add(node);
        }

        return (new AsmFunction(function.Name, function.IsEntryPoint, function.CcTier, nodes), changed);
    }

    private static (AsmFunction Function, bool Changed) FuseConditionalJumpOverSingleInstruction(AsmFunction function)
    {
        HashSet<string> targetedLabels = CollectJumpTargets(function.Nodes);
        List<AsmNode> nodes = [];
        bool changed = false;

        for (int i = 0; i < function.Nodes.Count;)
        {
            if (i + 2 < function.Nodes.Count
                && function.Nodes[i] is AsmInstructionNode jump
                && jump.Opcode == "JMP"
                && jump.Predicate is not null
                && jump.Operands.Count == 1
                && jump.Operands[0] is AsmSymbolOperand target
                && function.Nodes[i + 1] is AsmInstructionNode body
                && body.Predicate is null
                && function.Nodes[i + 2] is AsmLabelNode label
                && label.Name == target.Name
                && !targetedLabels.Contains(label.Name))
            {
                nodes.Add(new AsmInstructionNode(body.Opcode, body.Operands, InvertPredicate(jump.Predicate), body.FlagEffect));
                nodes.Add(label);
                changed = true;
                i += 3;
                continue;
            }

            nodes.Add(function.Nodes[i]);
            i++;
        }

        return (new AsmFunction(function.Name, function.IsEntryPoint, function.CcTier, nodes), changed);
    }

    private static (AsmFunction Function, bool Changed) FuseMuxConditionPair(AsmFunction function)
    {
        List<AsmNode> nodes = [];
        bool changed = false;

        for (int i = 0; i < function.Nodes.Count;)
        {
            if (i + 1 < function.Nodes.Count
                && function.Nodes[i] is AsmInstructionNode first
                && function.Nodes[i + 1] is AsmInstructionNode second
                && TryFuseMuxPair(first, second, out AsmInstructionNode? fused))
            {
                nodes.Add(fused!);
                changed = true;
                i += 2;
                continue;
            }

            nodes.Add(function.Nodes[i]);
            i++;
        }

        return (new AsmFunction(function.Name, function.IsEntryPoint, function.CcTier, nodes), changed);
    }

    private static (AsmFunction Function, bool Changed) ElideSemanticNops(AsmFunction function)
    {
        List<AsmNode> nodes = [];
        bool changed = false;

        foreach (AsmNode node in function.Nodes)
        {
            if (node is AsmInstructionNode instruction && IsSemanticNop(instruction))
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

        return P2InstructionMetadata.IsControlFlow(instruction.Opcode, instruction.Operands.Count)
            || HasImmediateSymbolOperand(instruction);
    }

    private static bool IsPureRegisterLocalInstruction(AsmInstructionNode instruction)
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

    private static HashSet<string> CollectJumpTargets(IReadOnlyList<AsmNode> nodes)
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

    private static bool HasImmediateSymbolOperand(AsmInstructionNode instruction)
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

    private static bool EndsWithTargetedLabel(IReadOnlyList<AsmNode> nodes, IReadOnlySet<string> targets)
        => nodes.Count > 0 && nodes[^1] is AsmLabelNode label && targets.Contains(label.Name);

    private static bool IsControlFlowInstruction(AsmInstructionNode instruction)
        => P2InstructionMetadata.IsControlFlow(instruction.Opcode, instruction.Operands.Count);

    private static bool TryFuseMuxPair(AsmInstructionNode first, AsmInstructionNode second, out AsmInstructionNode? fused)
    {
        fused = null;
        if (first.FlagEffect != AsmFlagEffect.None || second.FlagEffect != AsmFlagEffect.None)
            return false;

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

    private static bool IsSemanticNop(AsmInstructionNode instruction)
    {
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

    private static string InvertPredicate(string predicate)
        => predicate switch
        {
            "IF_Z" => "IF_NZ",
            "IF_NZ" => "IF_Z",
            "IF_C" => "IF_NC",
            "IF_NC" => "IF_C",
            _ => predicate,
        };

}
