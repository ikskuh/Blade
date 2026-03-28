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
    public static IReadOnlyDictionary<string, InlineAsmBindingAccess> ComputeBindingAccess(
        IReadOnlyList<InlineAsmLine> parsedLines,
        IReadOnlyCollection<string> bindingNames)
    {
        Requires.NotNull(parsedLines);
        Requires.NotNull(bindingNames);

        HashSet<string> bindingNameSet = new(bindingNames, StringComparer.Ordinal);
        Dictionary<string, InlineAsmBindingAccess> access = new(bindingNames.Count, StringComparer.Ordinal);
        HashSet<string> seenBindings = new(StringComparer.Ordinal);

        foreach (string bindingName in bindingNames)
            access[bindingName] = InlineAsmBindingAccess.ReadWrite;

        foreach (InlineAsmLine line in parsedLines)
        {
            if (line is not InlineAsmInstructionLine instruction)
                continue;

            if (!P2InstructionMetadata.TryGetInstructionForm(instruction.Mnemonic, instruction.Operands.Count, out _))
                return access;

            for (int operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
            {
                if (instruction.Operands[operandIndex] is not InlineAsmBindingRefOperand binding
                    || !bindingNameSet.Contains(binding.BindingName))
                {
                    continue;
                }

                P2OperandAccess operandAccess = P2InstructionMetadata.GetOperandAccess(
                    instruction.Mnemonic,
                    instruction.Operands.Count,
                    operandIndex);
                InlineAsmBindingAccess bindingAccess = ToBindingAccess(operandAccess);
                access[binding.BindingName] = seenBindings.Add(binding.BindingName)
                    ? bindingAccess
                    : Merge(access[binding.BindingName], bindingAccess);
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
