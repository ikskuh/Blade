using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Blade;
using Blade.Diagnostics;
using Blade.Source;

namespace Blade.Semantics;

/// <summary>
/// Validates inline assembly blocks against the Propeller 2 instruction set.
/// Parses asm body text into structured lines, validates instruction mnemonics,
/// condition prefixes, flag effects, and variable references.
/// </summary>
public static class InlineAssemblyValidator
{
    /// <summary>
    /// Represents a single parsed inline assembly instruction line.
    /// </summary>
    public sealed class AsmLine
    {
        public string? Condition { get; init; }
        public string Mnemonic { get; init; } = "";
        public IReadOnlyList<string> Operands { get; init; } = Array.Empty<string>();
        public string? FlagEffect { get; init; }
        public string RawText { get; init; } = "";
    }

    /// <summary>
    /// Result of validating an inline assembly block.
    /// </summary>
    public sealed class ValidationResult
    {
        public List<AsmLine> Lines { get; } = [];
        public List<string> ReferencedVariables { get; } = [];
        public bool IsValid { get; set; } = true;
    }

    /// <summary>
    /// Validates the body of an asm { ... } block.
    /// Reports diagnostics for unknown instructions, bad variable references, etc.
    /// Returns structured parse result for downstream codegen.
    /// </summary>
    public static ValidationResult Validate(
        string body,
        TextSpan blockSpan,
        HashSet<string> availableVariables,
        DiagnosticBag diagnostics)
    {
        Requires.NotNull(body);
        Requires.NotNull(availableVariables);
        Requires.NotNull(diagnostics);

        ValidationResult result = new();
        string[] rawLines = body.Split('\n');

        foreach (string rawLine in rawLines)
        {
            string trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            // Strip trailing comments
            int commentIdx = trimmed.IndexOf("//", StringComparison.Ordinal);
            if (commentIdx >= 0)
                trimmed = trimmed[..commentIdx].Trim();

            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            AsmLine? line = ParseAsmLine(trimmed, blockSpan, availableVariables, diagnostics, result);
            if (line is not null)
                result.Lines.Add(line);
            else
                result.IsValid = false;
        }

        return result;
    }

    private static AsmLine? ParseAsmLine(
        string text,
        TextSpan blockSpan,
        HashSet<string> availableVariables,
        DiagnosticBag diagnostics,
        ValidationResult result)
    {
        // Tokenize: split on whitespace, but respect {var} references and commas
        // Format: [CONDITION] MNEMONIC [operands...] [WC/WZ/WCZ]
        // Operands may contain {varname}, #immediate, register names

        string remaining = text;
        string? condition = null;
        string? flagEffect = null;

        // Check for condition prefix at the start
        string firstWord = GetFirstWord(remaining);
        if (P2InstructionMetadata.IsValidConditionPrefix(firstWord))
        {
            condition = firstWord.ToUpperInvariant();
            remaining = remaining[firstWord.Length..].TrimStart();
        }

        // Now extract the mnemonic
        string mnemonic = GetFirstWord(remaining);
        if (string.IsNullOrEmpty(mnemonic))
        {
            diagnostics.ReportInlineAsmEmptyInstruction(blockSpan);
            return null;
        }

        if (!P2InstructionMetadata.IsValidInstruction(mnemonic))
        {
            diagnostics.ReportInlineAsmUnknownInstruction(blockSpan, mnemonic);
            return null;
        }

        remaining = remaining[mnemonic.Length..].TrimStart();

        // Check for flag effects at the end
        // Need to look at the last word(s)
        string lastWord = GetLastWord(remaining);
        if (!string.IsNullOrEmpty(lastWord) && P2InstructionMetadata.IsValidFlagEffect(lastWord))
        {
            flagEffect = lastWord.ToUpperInvariant();
            remaining = remaining[..remaining.LastIndexOf(lastWord, StringComparison.OrdinalIgnoreCase)].TrimEnd();
        }

        // Parse operands (comma-separated, may contain {varname} or # immediates)
        List<string> operands = [];
        if (!string.IsNullOrWhiteSpace(remaining))
        {
            // Split by commas, trim each
            string[] parts = remaining.Split(',');
            foreach (string part in parts)
            {
                string op = part.Trim();
                if (!string.IsNullOrEmpty(op))
                    operands.Add(op);
            }
        }

        // Validate variable references in operands
        foreach (string operand in operands)
        {
            // Find all {varname} references
            MatchCollection matches = Regex.Matches(operand, @"\{(\w+)\}");
            foreach (Match match in matches)
            {
                string varName = match.Groups[1].Value;
                if (!availableVariables.Contains(varName))
                {
                    diagnostics.ReportInlineAsmUndefinedVariable(blockSpan, varName);
                    result.IsValid = false;
                }
                else if (!result.ReferencedVariables.Contains(varName))
                {
                    result.ReferencedVariables.Add(varName);
                }
            }
        }

        return new AsmLine
        {
            Condition = condition,
            Mnemonic = mnemonic.ToUpperInvariant(),
            Operands = new ReadOnlyCollection<string>(operands),
            FlagEffect = flagEffect,
            RawText = text,
        };
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
            return "";
        int i = trimmed.Length - 1;
        while (i >= 0 && !char.IsWhiteSpace(trimmed[i]) && trimmed[i] != ',')
            i--;
        return trimmed[(i + 1)..];
    }

    /// <summary>
    /// Returns true if the given mnemonic is a valid P2 instruction.
    /// </summary>
    public static bool IsValidInstruction(string mnemonic)
        => P2InstructionMetadata.IsValidInstruction(mnemonic);

    /// <summary>
    /// Returns true if the given name is a valid condition prefix.
    /// </summary>
    public static bool IsValidCondition(string name)
        => P2InstructionMetadata.IsValidConditionPrefix(name);
}
