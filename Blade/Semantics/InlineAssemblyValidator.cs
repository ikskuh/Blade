using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Blade.Diagnostics;
using Blade.Source;

namespace Blade.Semantics;

/// <summary>
/// Validates inline assembly blocks against the Propeller 2 instruction set.
/// Parses asm body text into structured lines and typed operands that downstream
/// stages can consume without reparsing raw strings.
/// </summary>
public static class InlineAssemblyValidator
{
    public sealed class ValidationResult
    {
        public Collection<InlineAsmLine> Lines { get; } = new();
        public Collection<InlineAsmBindingSlot> ReferencedBindings { get; } = new();
        public bool IsValid { get; set; } = true;
    }

    public static ValidationResult Validate(
        string body,
        TextSpan blockSpan,
        IReadOnlyDictionary<string, InlineAsmBindingSlot> availableBindings,
        DiagnosticBag diagnostics)
    {
        Requires.NotNull(body);
        Requires.NotNull(availableBindings);
        Requires.NotNull(diagnostics);

        ValidationResult result = new();
        string[] rawLines = body.Split('\n');
        IReadOnlyDictionary<string, ControlFlowLabelSymbol> labels = CollectLabelDefinitions(rawLines);

        foreach (string rawLine in rawLines)
        {
            string? trimmed = NormalizeInstructionText(rawLine);
            if (trimmed is null)
                continue;

            InlineAsmLine? line = ParseAsmLine(trimmed, blockSpan, availableBindings, labels, diagnostics, result);
            if (line is not null)
            {
                result.Lines.Add(line);
            }
            else
            {
                result.IsValid = false;
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, ControlFlowLabelSymbol> CollectLabelDefinitions(
        IReadOnlyList<string> rawLines)
    {
        Dictionary<string, ControlFlowLabelSymbol> labels = new(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in rawLines)
        {
            string? trimmed = NormalizeInstructionText(rawLine);
            if (trimmed is null || !TryParseLabelName(trimmed, out string? labelName))
                continue;

            if (!labels.TryAdd(labelName!, new ControlFlowLabelSymbol(labelName!)))
                continue;
        }

        return labels;
    }

    private static InlineAsmLine? ParseAsmLine(
        string text,
        TextSpan blockSpan,
        IReadOnlyDictionary<string, InlineAsmBindingSlot> availableBindings,
        IReadOnlyDictionary<string, ControlFlowLabelSymbol> labels,
        DiagnosticBag diagnostics,
        ValidationResult result)
    {
        if (TryParseLabelName(text, out string? labelName))
            return new InlineAsmLabelLine(labels[labelName!], text);

        string remaining = text;
        P2ConditionCode? condition = null;
        P2FlagEffect? flagEffect = null;

        string firstWord = GetFirstWord(remaining);
        if (P2InstructionMetadata.TryParseConditionCode(firstWord, out P2ConditionCode parsedCondition))
        {
            condition = parsedCondition;
            remaining = remaining[firstWord.Length..].TrimStart();
        }

        string mnemonicText = GetFirstWord(remaining);
        if (string.IsNullOrEmpty(mnemonicText))
        {
            diagnostics.ReportInlineAsmEmptyInstruction(blockSpan);
            return null;
        }

        if (!P2InstructionMetadata.TryParseMnemonic(mnemonicText, out P2Mnemonic mnemonic))
        {
            diagnostics.ReportInlineAsmUnknownInstruction(blockSpan, mnemonicText);
            return null;
        }

        remaining = remaining[mnemonicText.Length..].TrimStart();

        string lastWord = GetLastWord(remaining);
        if (!string.IsNullOrEmpty(lastWord) && P2InstructionMetadata.TryParseFlagEffect(lastWord, out P2FlagEffect parsedFlagEffect))
        {
            flagEffect = parsedFlagEffect;
            remaining = remaining[..remaining.LastIndexOf(lastWord, StringComparison.OrdinalIgnoreCase)].TrimEnd();
        }

        List<InlineAsmOperand> operands = [];
        if (!string.IsNullOrWhiteSpace(remaining))
        {
            string[] parts = remaining.Split(',');
            foreach (string part in parts)
            {
                string operandText = part.Trim();
                if (operandText.Length == 0)
                    continue;

                InlineAsmOperand? operand = ParseOperand(operandText, blockSpan, availableBindings, labels, diagnostics, result);
                if (operand is null)
                    return null;

                operands.Add(operand);
            }
        }

        if (!P2InstructionMetadata.TryGetInstructionForm(mnemonic, operands.Count, out _))
        {
            diagnostics.ReportInlineAsmInvalidInstructionForm(blockSpan, P2InstructionMetadata.GetMnemonicText(mnemonic), operands.Count);
            return null;
        }

        return new InlineAsmInstructionLine(condition, mnemonic, new ReadOnlyCollection<InlineAsmOperand>(operands), flagEffect, text);
    }

    private static InlineAsmOperand? ParseOperand(
        string operandText,
        TextSpan blockSpan,
        IReadOnlyDictionary<string, InlineAsmBindingSlot> availableBindings,
        IReadOnlyDictionary<string, ControlFlowLabelSymbol> labels,
        DiagnosticBag diagnostics,
        ValidationResult result)
    {
        string trimmed = operandText.Trim();
        if (trimmed.Length == 0)
            return null;

        if (TryParseBindingReference(trimmed, out string? bindingName))
        {
            if (!availableBindings.TryGetValue(bindingName!, out InlineAsmBindingSlot? bindingSlot))
            {
                diagnostics.ReportInlineAsmUndefinedVariable(blockSpan, bindingName!);
                result.IsValid = false;
                return null;
            }

            AddReferencedBinding(result, bindingSlot);
            return new InlineAsmBindingRefOperand(bindingSlot);
        }

        if (trimmed.Contains('{', StringComparison.Ordinal) || trimmed.Contains('}', StringComparison.Ordinal))
            return null;

        if (trimmed.StartsWith('#'))
        {
            string immediateText = trimmed[1..].Trim();
            if (immediateText == "$")
                return new InlineAsmCurrentAddressOperand(InlineAsmAddressingMode.Immediate);

            if (TryParseImmediate(immediateText, out long immediate))
                return new InlineAsmImmediateOperand(immediate);

            if (labels.TryGetValue(immediateText, out ControlFlowLabelSymbol? label))
                return new InlineAsmLabelOperand(label, InlineAsmAddressingMode.Immediate);

            diagnostics.ReportInlineAsmUndefinedLabel(blockSpan, immediateText);
            result.IsValid = false;
            return null;
        }

        if (trimmed == "$")
            return new InlineAsmCurrentAddressOperand(InlineAsmAddressingMode.Direct);

        if (!IsPlainInlineAsmSymbol(trimmed.AsSpan()))
        {
            diagnostics.ReportInlineAsmUndefinedLabel(blockSpan, trimmed);
            result.IsValid = false;
            return null;
        }

        if (labels.TryGetValue(trimmed, out ControlFlowLabelSymbol? directLabel))
            return new InlineAsmLabelOperand(directLabel, InlineAsmAddressingMode.Direct);

        if (P2InstructionMetadata.TryParseSpecialRegister(trimmed, out P2SpecialRegister register))
            return new InlineAsmSpecialRegisterOperand(register);

        if (availableBindings.TryGetValue(trimmed, out InlineAsmBindingSlot? directBinding))
        {
            AddReferencedBinding(result, directBinding);
            return new InlineAsmBindingRefOperand(directBinding);
        }

        diagnostics.ReportInlineAsmUndefinedLabel(blockSpan, trimmed);
        result.IsValid = false;
        return null;
    }

    private static void AddReferencedBinding(ValidationResult result, InlineAsmBindingSlot bindingSlot)
    {
        if (!result.ReferencedBindings.Contains(bindingSlot))
            result.ReferencedBindings.Add(bindingSlot);
    }

    private static string? NormalizeInstructionText(string rawLine)
    {
        string trimmed = rawLine.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        int commentIndex = trimmed.IndexOf("//", StringComparison.Ordinal);
        if (commentIndex >= 0)
            trimmed = trimmed[..commentIndex].Trim();

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static bool TryParseLabelName(string text, out string? labelName)
    {
        labelName = null;
        ReadOnlySpan<char> trimmed = text.AsSpan().Trim();
        if (trimmed.Length < 2 || trimmed[^1] != ':')
            return false;

        ReadOnlySpan<char> candidate = trimmed[..^1].Trim();
        if (candidate.IsEmpty || !IsPlainInlineAsmSymbol(candidate))
            return false;

        string candidateText = candidate.ToString();
        if (P2InstructionMetadata.TryParseConditionCode(candidateText, out _)
            || P2InstructionMetadata.TryParseMnemonic(candidateText, out _)
            || P2InstructionMetadata.TryParseFlagEffect(candidateText, out _))
        {
            return false;
        }

        labelName = candidateText;
        return true;
    }

    private static bool TryParseBindingReference(string text, out string? bindingName)
    {
        bindingName = null;
        if (text.Length < 2 || text[0] != '{' || text[^1] != '}')
            return false;

        string name = text[1..^1].Trim();
        if (name.Length == 0
            || name.Contains('{', StringComparison.Ordinal)
            || name.Contains('}', StringComparison.Ordinal))
        {
            return false;
        }

        bindingName = name;
        return true;
    }

    private static bool IsPlainInlineAsmSymbol(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_' || c == '$')
                continue;

            return false;
        }

        return text.Length > 0;
    }

    private static string GetFirstWord(string text)
    {
        int i = 0;
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != ',')
            i++;
        return text[..i];
    }

    private static string GetLastWord(string text)
    {
        string trimmed = text.TrimEnd();
        if (string.IsNullOrEmpty(trimmed))
            return string.Empty;

        int i = trimmed.Length - 1;
        while (i >= 0 && !char.IsWhiteSpace(trimmed[i]) && trimmed[i] != ',')
            i--;
        return trimmed[(i + 1)..];
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
