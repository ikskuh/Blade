using System;
using System.Collections.Generic;
using Blade;

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
        IReadOnlyList<InlineAssemblyValidator.AsmLine> parsedLines,
        IReadOnlyCollection<string> bindingNames)
    {
        Requires.NotNull(parsedLines);
        Requires.NotNull(bindingNames);

        HashSet<string> bindingNameSet = new(bindingNames, StringComparer.Ordinal);
        Dictionary<string, InlineAsmBindingAccess> access = new(bindingNames.Count, StringComparer.Ordinal);

        if (bindingNames.Count == 0)
            return access;

        if (!CanLowerTypedLosslessly(parsedLines, bindingNameSet))
        {
            foreach (string bindingName in bindingNames)
                access[bindingName] = InlineAsmBindingAccess.ReadWrite;
            return access;
        }

        foreach (InlineAssemblyValidator.AsmLine line in parsedLines)
        {
            if (line.Mnemonic is not P2Mnemonic mnemonic)
                continue;

            if (!TryGetOperandAccesses(mnemonic, line.Operands.Count, out InlineAsmBindingAccess[]? operandAccesses))
            {
                foreach (string bindingName in bindingNames)
                    access[bindingName] = InlineAsmBindingAccess.ReadWrite;
                return access;
            }

            for (int i = 0; i < line.Operands.Count; i++)
            {
                if (!TryGetBindingName(line.Operands[i], out string? bindingName))
                    continue;

                if (bindingName is null || !bindingNameSet.Contains(bindingName))
                    continue;

                if (access.TryGetValue(bindingName, out InlineAsmBindingAccess existing))
                    access[bindingName] = Merge(existing, operandAccesses![i]);
                else
                    access[bindingName] = operandAccesses![i];
            }
        }

        foreach (string bindingName in bindingNames)
        {
            if (!access.ContainsKey(bindingName))
                access[bindingName] = InlineAsmBindingAccess.ReadWrite;
        }

        return access;
    }

    private static bool CanLowerTypedLosslessly(
        IReadOnlyList<InlineAssemblyValidator.AsmLine> parsedLines,
        IReadOnlySet<string> bindingNames)
    {
        HashSet<string> definedLabels = CollectDefinedLabels(parsedLines);

        foreach (InlineAssemblyValidator.AsmLine line in parsedLines)
        {
            foreach (string operand in line.Operands)
            {
                if (!IsTypedInlineAsmOperand(operand, bindingNames, definedLabels))
                    return false;
            }
        }

        return true;
    }

    private static bool IsTypedInlineAsmOperand(string text, IReadOnlySet<string> bindingNames, IReadOnlySet<string> definedLabels)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
            return false;

        if (TryGetBindingName(trimmed, out string? bindingName))
            return bindingName is not null && bindingNames.Contains(bindingName);

        if (trimmed.Contains('{', StringComparison.Ordinal) || trimmed.Contains('}', StringComparison.Ordinal))
            return false;

        if (trimmed.StartsWith('#'))
        {
            string immediateText = trimmed[1..].Trim();
            if (immediateText == "$")
                return true;

            return TryParseImmediate(immediateText, out _) || IsKnownSymbol(immediateText, definedLabels);
        }

        if (trimmed.EndsWith(':'))
            return IsPlainSymbol(trimmed[..^1].Trim());

        return trimmed == "$" || IsKnownSymbol(trimmed, definedLabels);
    }

    private static bool IsKnownSymbol(string text, IReadOnlySet<string> definedLabels)
    {
        if (!IsPlainSymbol(text))
            return false;

        return definedLabels.Contains(text)
            || Enum.TryParse<P2SpecialRegister>(text, ignoreCase: true, out _);
    }

    private static HashSet<string> CollectDefinedLabels(IReadOnlyList<InlineAssemblyValidator.AsmLine> parsedLines)
    {
        HashSet<string> labels = new(StringComparer.OrdinalIgnoreCase);
        foreach (InlineAssemblyValidator.AsmLine line in parsedLines)
        {
            if (line.IsLabel && !string.IsNullOrWhiteSpace(line.LabelName))
                labels.Add(line.LabelName);
        }

        return labels;
    }

    private static bool TryGetBindingName(string text, out string? bindingName)
    {
        bindingName = null;
        string trimmed = text.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[^1] != '}')
            return false;

        string name = trimmed[1..^1].Trim();
        if (name.Length == 0
            || name.Contains('{', StringComparison.Ordinal)
            || name.Contains('}', StringComparison.Ordinal))
        {
            return false;
        }

        bindingName = name;
        return true;
    }

    private static bool TryGetOperandAccesses(
        P2Mnemonic mnemonic,
        int operandCount,
        out InlineAsmBindingAccess[]? operandAccesses)
    {
        operandAccesses = null;
        if (!P2InstructionMetadata.TryGetInstructionForm(mnemonic, operandCount, out _))
            return false;

        InlineAsmBindingAccess[] accesses = new InlineAsmBindingAccess[operandCount];
        for (int operandIndex = 0; operandIndex < operandCount; operandIndex++)
        {
            P2OperandAccess access = P2InstructionMetadata.GetOperandAccess(mnemonic, operandCount, operandIndex);
            accesses[operandIndex] = access switch
            {
                P2OperandAccess.Read => InlineAsmBindingAccess.Read,
                P2OperandAccess.Write => InlineAsmBindingAccess.Write,
                P2OperandAccess.ReadWrite => InlineAsmBindingAccess.ReadWrite,
                _ => InlineAsmBindingAccess.ReadWrite,
            };
        }

        operandAccesses = accesses;
        return true;
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

    private static bool IsPlainSymbol(string text)
    {
        foreach (char c in text)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_' || c == '$')
                continue;
            return false;
        }

        return text.Length > 0;
    }

    private static bool TryParseImmediate(string text, out long value)
    {
        value = 0;
        if (text.Length == 0)
            return false;

        bool negative = false;
        string remainder = text;
        if (remainder[0] is '+' or '-')
        {
            negative = remainder[0] == '-';
            remainder = remainder[1..];
        }

        remainder = remainder.Replace("_", "", StringComparison.Ordinal);
        if (remainder.Length == 0)
            return false;

        try
        {
            if (remainder.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(remainder[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out long hex))
                    return false;
                value = negative ? -hex : hex;
                return true;
            }

            if (remainder.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                long binary = Convert.ToInt64(remainder[2..], 2);
                value = negative ? -binary : binary;
                return true;
            }

            if (!long.TryParse(remainder, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long decimalValue))
                return false;

            value = negative ? -decimalValue : decimalValue;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
