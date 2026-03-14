using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Blade.IR;

namespace Blade.IR.Asm;

public static class FinalAssemblyWriter
{
    /// <summary>
    /// Opcodes where the S-field symbol operand is a jump/call target address
    /// (needs # prefix). All other symbol operands are register references.
    /// </summary>
    private static bool IsDataDirective(string text)
    {
        ReadOnlySpan<char> t = text.AsSpan().TrimStart();
        return t.StartsWith("LONG", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("WORD", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("BYTE", StringComparison.OrdinalIgnoreCase);
    }

    public static string Write(AsmModule module)
    {
        StringBuilder sb = new();
        WriteConBlock(sb, module);
        sb.AppendLine("DAT");
        sb.AppendLine("    org 0");
        sb.AppendLine("    ' --- Blade compiler output ---");

        foreach (AsmFunction function in module.Functions)
        {
            sb.AppendLine();
            sb.Append("    ' function ");
            sb.Append(function.Name);
            sb.Append(" (");
            sb.Append(function.CcTier);
            sb.AppendLine(")");
            sb.Append("  ");
            sb.AppendLine(FormatIdentifier(function.Name));
            WriteFunctionNodes(sb, function.Nodes);
        }

        return sb.ToString();
    }

    private static void WriteConBlock(StringBuilder sb, AsmModule module)
    {
        bool wroteHeader = false;
        foreach (StoragePlace place in module.StoragePlaces)
        {
            if (place.Kind != StoragePlaceKind.FixedRegisterAlias || !place.FixedAddress.HasValue)
                continue;

            if (P2InstructionMetadata.IsSpecialRegisterName(place.EmittedName))
                continue;

            if (!wroteHeader)
            {
                sb.AppendLine("CON");
                wroteHeader = true;
            }

            sb.Append("    ");
            sb.Append(FormatIdentifier(place.EmittedName));
            sb.Append(" = 0x");
            sb.Append(place.FixedAddress.Value.ToString("X", CultureInfo.InvariantCulture));
            sb.AppendLine();
        }

        if (wroteHeader)
            sb.AppendLine();
    }

    private static void WriteFunctionNodes(StringBuilder sb, IReadOnlyList<AsmNode> nodes)
    {
        int index = 0;
        while (index < nodes.Count)
        {
            if (TryWriteRegisterFile(sb, nodes, ref index))
                continue;

            if (TryWriteRawInlineAsmBlock(sb, nodes, ref index))
                continue;

            WriteNode(sb, nodes[index]);
            index++;
        }
    }

    private static bool TryWriteRegisterFile(StringBuilder sb, IReadOnlyList<AsmNode> nodes, ref int index)
    {
        if (nodes[index] is not AsmCommentNode { Text: "--- register file ---" })
            return false;

        List<(string Label, string Directive, string Value)> rows = [];
        int rowIndex = index + 1;
        while (rowIndex + 1 < nodes.Count
            && nodes[rowIndex] is AsmLabelNode label
            && nodes[rowIndex + 1] is AsmDirectiveNode directive
            && TryParseDataDirective(directive.Text, out string directiveName, out string valueText))
        {
            rows.Add((label.Name, directiveName, valueText));
            rowIndex += 2;
        }

        sb.AppendLine();
        sb.AppendLine("' --- register file ---");
        if (rows.Count == 0)
        {
            index = rowIndex;
            return true;
        }

        int maxLabelWidth = rows.Max(static row => row.Label.Length);
        int maxDirectiveWidth = rows.Max(static row => row.Directive.Length);
        int maxValueWidth = rows.Max(static row => row.Value.Length);

        foreach ((string label, string directive, string value) in rows)
        {
            sb.Append(FormatIdentifier(label).PadRight(maxLabelWidth));
            sb.Append(' ');
            sb.Append(directive.PadRight(maxDirectiveWidth));
            sb.Append(' ');
            sb.Append(value.PadLeft(maxValueWidth));
            sb.AppendLine();
        }

        index = rowIndex;
        return true;
    }

    private static bool TryWriteRawInlineAsmBlock(StringBuilder sb, IReadOnlyList<AsmNode> nodes, ref int index)
    {
        if (nodes[index] is not AsmCommentNode beginComment
            || !beginComment.Text.EndsWith(" begin", StringComparison.Ordinal)
            || index + 1 >= nodes.Count
            || nodes[index + 1] is not AsmInlineTextNode)
        {
            return false;
        }

        int endIndex = index + 1;
        while (endIndex < nodes.Count && nodes[endIndex] is AsmInlineTextNode)
            endIndex++;

        if (endIndex >= nodes.Count
            || nodes[endIndex] is not AsmCommentNode endComment
            || endComment.Text != beginComment.Text[..^" begin".Length] + " end")
        {
            return false;
        }

        WriteNode(sb, beginComment);

        int commonIndent = GetCommonInlineTextIndent(nodes, index + 1, endIndex);
        for (int i = index + 1; i < endIndex; i++)
        {
            AsmInlineTextNode inlineText = (AsmInlineTextNode)nodes[i];
            WriteInlineText(sb, inlineText.Text, commonIndent);
        }

        WriteNode(sb, endComment);
        index = endIndex + 1;
        return true;
    }

    private static int GetCommonInlineTextIndent(IReadOnlyList<AsmNode> nodes, int start, int end)
    {
        int? commonIndent = null;
        for (int i = start; i < end; i++)
        {
            string text = ((AsmInlineTextNode)nodes[i]).Text;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            int indent = CountLeadingWhitespace(text);
            commonIndent = commonIndent.HasValue ? Math.Min(commonIndent.Value, indent) : indent;
        }

        return commonIndent ?? 0;
    }

    private static int CountLeadingWhitespace(string text)
    {
        int count = 0;
        while (count < text.Length && char.IsWhiteSpace(text[count]))
            count++;
        return count;
    }

    private static void WriteInlineText(StringBuilder sb, string text, int trimIndent)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            sb.AppendLine();
            return;
        }

        int removeCount = Math.Min(trimIndent, CountLeadingWhitespace(text));
        sb.Append("    ");
        sb.AppendLine(text[removeCount..]);
    }

    private static bool TryParseDataDirective(string text, out string directiveName, out string valueText)
    {
        directiveName = string.Empty;
        valueText = string.Empty;

        ReadOnlySpan<char> trimmed = text.AsSpan().Trim();
        if (trimmed.IsEmpty)
            return false;

        int separator = trimmed.IndexOf(' ');
        if (separator < 0)
            return false;

        directiveName = trimmed[..separator].ToString();
        valueText = trimmed[(separator + 1)..].TrimStart().ToString();
        return IsDataDirective(directiveName);
    }

    private static void WriteNode(StringBuilder sb, AsmNode node)
    {
        switch (node)
        {
            case AsmDirectiveNode directive:
                // Only emit data storage directives (LONG, WORD, BYTE).
                // Internal metadata markers like "function $top" are silently dropped.
                if (IsDataDirective(directive.Text))
                {
                    sb.Append("    ");
                    sb.AppendLine(directive.Text);
                }
                break;

            case AsmLabelNode label:
                sb.Append("  ");
                sb.AppendLine(FormatIdentifier(label.Name));
                break;

            case AsmCommentNode comment:
                sb.Append("    ' ");
                sb.AppendLine(comment.Text);
                break;

            case AsmImplicitUseNode:
                break;

            case AsmInlineTextNode inlineText:
                WriteInlineText(sb, inlineText.Text, trimIndent: 0);
                break;

            case AsmInstructionNode instruction:
                sb.Append("    ");
                if (!string.IsNullOrWhiteSpace(instruction.Predicate))
                {
                    sb.Append(instruction.Predicate);
                    sb.Append(' ');
                }

                sb.Append(instruction.Opcode);
                if (instruction.Operands.Count > 0)
                {
                    sb.Append(' ');
                    for (int i = 0; i < instruction.Operands.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(", ");
                        sb.Append(FormatOperand(instruction, i));
                    }
                }

                if (instruction.FlagEffect != AsmFlagEffect.None)
                {
                    sb.Append(' ');
                    sb.Append(FormatFlagEffect(instruction.FlagEffect));
                }

                sb.AppendLine();
                break;
        }
    }

    private static string FormatOperand(AsmInstructionNode instruction, int operandIndex)
    {
        AsmOperand operand = instruction.Operands[operandIndex];

        return operand switch
        {
            AsmPhysicalRegisterOperand phys => phys.Name,
            AsmRegisterOperand virt => virt.Format(),
            AsmImmediateOperand imm => imm.Format(),
            AsmPlaceOperand place => FormatIdentifier(place.Place.EmittedName),
            AsmSymbolOperand sym => FormatSymbolOperand(sym, instruction, operandIndex),
            _ => operand.Format(),
        };
    }

    /// <summary>
    /// Format a symbol operand with or without # prefix depending on context.
    /// - Jump/call targets in S-field: #label (immediate address)
    /// - Register references in D-field or S-field: plain label
    /// - Special registers (PA, PB, etc.): plain name
    /// </summary>
    private static string FormatSymbolOperand(
        AsmSymbolOperand sym,
        AsmInstructionNode instruction,
        int operandIndex)
    {
        // Special register names: always plain
        if (P2InstructionMetadata.IsSpecialRegisterName(sym.Name))
            return sym.Name;

        // $ (current address): always prefixed
        if (sym.Name == "$")
            return "#$";

        // Jump/call opcodes: the target (last operand) gets # prefix
        if (P2InstructionMetadata.RequiresImmediateAddressPrefix(instruction.Opcode, instruction.Operands.Count, operandIndex))
        {
            return $"#{FormatIdentifier(sym.Name)}";
        }

        // Default: register reference (no # prefix)
        return FormatIdentifier(sym.Name);
    }

    private static string FormatFlagEffect(AsmFlagEffect effect)
    {
        return effect switch
        {
            AsmFlagEffect.WC => "WC",
            AsmFlagEffect.WZ => "WZ",
            AsmFlagEffect.WCZ => "WCZ",
            _ => string.Empty,
        };
    }

    private static string FormatIdentifier(string name)
    {
        if (P2InstructionMetadata.IsSpecialRegisterName(name))
            return name;

        StringBuilder builder = new();
        if (name.Length == 0)
            return "l_";

        char first = name[0];
        if (char.IsLetter(first) || first == '_')
        {
            builder.Append(first);
        }
        else
        {
            builder.Append('l');
            builder.Append(char.IsLetterOrDigit(first) || first == '_' ? first : '_');
        }

        for (int i = 1; i < name.Length; i++)
        {
            char ch = name[i];
            bool isValidLater = char.IsLetterOrDigit(ch) || ch == '_';
            builder.Append(isValidLater ? ch : '_');
        }

        return builder.ToString();
    }
}
