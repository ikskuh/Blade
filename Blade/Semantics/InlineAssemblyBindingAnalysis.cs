using System;
using System.Collections.Generic;

namespace Blade.Semantics;

public enum InlineAsmBindingAccess
{
    Read,
    Write,
    ReadWrite,
}

public static class InlineAssemblyBindingAnalysis
{
    public static IReadOnlyDictionary<InlineAsmBindingSlot, InlineAsmBindingAccess> ComputeBindingAccess(
        IReadOnlyList<InlineAsmLine> parsedLines,
        IReadOnlyCollection<InlineAsmBindingSlot> bindings)
    {
        Requires.NotNull(parsedLines);
        Requires.NotNull(bindings);

        HashSet<InlineAsmBindingSlot> bindingSet = new(bindings);
        Dictionary<InlineAsmBindingSlot, InlineAsmBindingAccess> access = new(bindings.Count);
        HashSet<InlineAsmBindingSlot> seenBindings = [];

        foreach (InlineAsmBindingSlot binding in bindings)
            access[binding] = InlineAsmBindingAccess.ReadWrite;

        foreach (InlineAsmLine line in parsedLines)
        {
            if (line is not InlineAsmInstructionLine instruction)
                continue;

            if (!P2InstructionMetadata.TryGetInstructionForm(instruction.Mnemonic, instruction.Operands.Count, out _))
                return access;

            for (int operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
            {
                if (instruction.Operands[operandIndex] is not InlineAsmBindingRefOperand binding
                    || !bindingSet.Contains(binding.Slot))
                {
                    continue;
                }

                P2OperandAccess operandAccess = P2InstructionMetadata.GetOperandAccess(
                    instruction.Mnemonic,
                    instruction.Operands.Count,
                    operandIndex);
                InlineAsmBindingAccess bindingAccess = ToBindingAccess(operandAccess);
                access[binding.Slot] = seenBindings.Add(binding.Slot)
                    ? bindingAccess
                    : Merge(access[binding.Slot], bindingAccess);
            }
        }

        return access;
    }

    private static InlineAsmBindingAccess ToBindingAccess(P2OperandAccess access)
    {
        return access switch
        {
            P2OperandAccess.Read => InlineAsmBindingAccess.Read,
            P2OperandAccess.Write => InlineAsmBindingAccess.Write,
            P2OperandAccess.ReadWrite => InlineAsmBindingAccess.ReadWrite,
            _ => InlineAsmBindingAccess.ReadWrite,
        };
    }

    private static InlineAsmBindingAccess Merge(InlineAsmBindingAccess current, InlineAsmBindingAccess next)
    {
        if (current == next)
            return current;

        return current switch
        {
            InlineAsmBindingAccess.Read when next == InlineAsmBindingAccess.Write => InlineAsmBindingAccess.ReadWrite,
            InlineAsmBindingAccess.Write when next == InlineAsmBindingAccess.Read => InlineAsmBindingAccess.ReadWrite,
            _ => InlineAsmBindingAccess.ReadWrite,
        };
    }

    public static bool IncludesRead(InlineAsmBindingAccess access)
        => access is InlineAsmBindingAccess.Read or InlineAsmBindingAccess.ReadWrite;

    public static bool IncludesWrite(InlineAsmBindingAccess access)
        => access is InlineAsmBindingAccess.Write or InlineAsmBindingAccess.ReadWrite;
}
