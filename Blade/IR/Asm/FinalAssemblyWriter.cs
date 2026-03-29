using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Blade;
using Blade.IR;
using Blade.Semantics;

namespace Blade.IR.Asm;

public static class FinalAssemblyWriter
{
    private static bool IsDataDirective(string text)
    {
        ReadOnlySpan<char> t = text.AsSpan().TrimStart();
        return t.StartsWith("LONG", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("WORD", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("BYTE", StringComparison.OrdinalIgnoreCase);
    }

    public static string Write(AsmModule module)
    {
        Requires.NotNull(module);

        HashSet<string> functionNames = module.Functions
            .Select(static function => function.Name)
            .ToHashSet(StringComparer.Ordinal);

        StringBuilder sb = new();
        WriteConBlock(sb, module, functionNames);
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
            sb.AppendLine(FormatFunctionIdentifier(function.Name));
            WriteFunctionNodes(sb, function.Nodes, functionNames);
        }

        return sb.ToString();
    }

    private static void WriteConBlock(StringBuilder sb, AsmModule module, IReadOnlySet<string> functionNames)
    {
        bool wroteHeader = false;
        foreach (StoragePlace place in module.StoragePlaces)
        {
            if (place.Kind is not (StoragePlaceKind.FixedRegisterAlias
                    or StoragePlaceKind.FixedLutAlias
                    or StoragePlaceKind.FixedHubAlias)
                || !place.FixedAddress.HasValue)
            {
                continue;
            }

            if (place.SpecialRegisterAlias is not null)
                continue;

            if (!wroteHeader)
            {
                sb.AppendLine("CON");
                wroteHeader = true;
            }

            sb.Append("    ");
            sb.Append(FormatIdentifier(place.EmittedName, functionNames));
            sb.Append(" = $");
            sb.Append(place.FixedAddress.Value.ToString("X", CultureInfo.InvariantCulture));
            sb.AppendLine();
        }

        if (wroteHeader)
            sb.AppendLine();
    }

    private static void WriteFunctionNodes(StringBuilder sb, IReadOnlyList<AsmNode> nodes, IReadOnlySet<string> functionNames)
    {
        int index = 0;
        while (index < nodes.Count)
        {
            if (TryWriteRegisterFile(sb, nodes, ref index, functionNames))
                continue;

            if (TryWriteLutFile(sb, nodes, ref index, functionNames))
                continue;

            if (TryWriteHubFile(sb, nodes, ref index, functionNames))
                continue;

            if (TryWriteConstantFile(sb, nodes, ref index, functionNames))
                continue;

            WriteNode(sb, nodes[index], functionNames);
            index++;
        }
    }

    private static bool TryWriteRegisterFile(
        StringBuilder sb,
        IReadOnlyList<AsmNode> nodes,
        ref int index,
        IReadOnlySet<string> functionNames)
    {
        if (nodes[index] is not AsmSectionNode { Section: AsmStorageSection.Register })
            return false;

        List<(string Label, string Directive, string Value)> rows = [];
        int rowIndex = index + 1;
        while (rowIndex + 1 < nodes.Count
            && nodes[rowIndex] is AsmLabelNode label
            && nodes[rowIndex + 1] is AsmDataNode data)
        {
            rows.Add((label.Name, FormatDataDirective(data.Directive), FormatDataValue(data, functionNames)));
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
            sb.Append(FormatIdentifier(label, functionNames).PadRight(maxLabelWidth));
            sb.Append(' ');
            sb.Append(directive.PadRight(maxDirectiveWidth));
            sb.Append(' ');
            sb.Append(value.PadLeft(maxValueWidth));
            sb.AppendLine();
        }

        index = rowIndex;
        return true;
    }


    private static bool TryWriteDataFileSection(
        StringBuilder sb,
        IReadOnlyList<AsmNode> nodes,
        ref int index,
        AsmStorageSection section,
        string sectionHeader,
        IReadOnlySet<string> functionNames)
    {
        if (nodes[index] is not AsmSectionNode sectionNode || sectionNode.Section != section)
            return false;

        List<(string Label, string Directive, string Value)> rows = [];
        int rowIndex = index + 1;
        while (rowIndex + 1 < nodes.Count
            && nodes[rowIndex] is AsmLabelNode label
            && nodes[rowIndex + 1] is AsmDataNode data)
        {
            rows.Add((label.Name, FormatDataDirective(data.Directive), FormatDataValue(data, functionNames)));
            rowIndex += 2;
        }

        sb.AppendLine();
        sb.AppendLine(sectionHeader);
        if (rows.Count > 0)
        {
            int maxLabelWidth = rows.Max(static row => row.Label.Length);
            int maxDirectiveWidth = rows.Max(static row => row.Directive.Length);
            int maxValueWidth = rows.Max(static row => row.Value.Length);

            foreach ((string label, string directive, string value) in rows)
            {
                sb.Append(FormatIdentifier(label, functionNames).PadRight(maxLabelWidth));
                sb.Append(' ');
                sb.Append(directive.PadRight(maxDirectiveWidth));
                sb.Append(' ');
                sb.Append(value.PadLeft(maxValueWidth));
                sb.AppendLine();
            }
        }

        index = rowIndex;
        return true;
    }

    private static bool TryWriteLutFile(
        StringBuilder sb,
        IReadOnlyList<AsmNode> nodes,
        ref int index,
        IReadOnlySet<string> functionNames)
    {
        if (nodes[index] is not AsmSectionNode { Section: AsmStorageSection.Lut })
            return false;

        // COG code/data must fit within the first 512 longs; LUT starts at $200.
        sb.AppendLine();
        sb.AppendLine("    fit $200");
        sb.AppendLine("    org $200");

        return TryWriteDataFileSection(sb, nodes, ref index, AsmStorageSection.Lut, "' --- lut file ---", functionNames);
    }

    private static bool TryWriteHubFile(
        StringBuilder sb,
        IReadOnlyList<AsmNode> nodes,
        ref int index,
        IReadOnlySet<string> functionNames)
    {
        if (nodes[index] is not AsmSectionNode { Section: AsmStorageSection.Hub })
            return false;

        // Switch from COG/LUT addressing to hub addressing.
        sb.AppendLine();
        sb.AppendLine("    orgh");

        return TryWriteDataFileSection(sb, nodes, ref index, AsmStorageSection.Hub, "' --- hub file ---", functionNames);
    }

    private static bool TryWriteConstantFile(
        StringBuilder sb,
        IReadOnlyList<AsmNode> nodes,
        ref int index,
        IReadOnlySet<string> functionNames)
    {
        if (nodes[index] is not AsmSectionNode { Section: AsmStorageSection.Constant })
            return false;

        List<(string Label, string Directive, string Value)> rows = [];
        int rowIndex = index + 1;
        while (rowIndex + 1 < nodes.Count
            && nodes[rowIndex] is AsmLabelNode label
            && nodes[rowIndex + 1] is AsmDataNode data)
        {
            rows.Add((label.Name, FormatDataDirective(data.Directive), FormatDataValue(data, functionNames)));
            rowIndex += 2;
        }

        sb.AppendLine();
        sb.AppendLine("' --- constant file ---");
        if (rows.Count == 0)
        {
            index = rowIndex;
            return true;
        }

        int maxLabelWidth = rows.Max(static row => row.Label.Length);
        int maxDirectiveWidth = rows.Max(static row => row.Directive.Length);

        foreach ((string label, string directive, string value) in rows)
        {
            sb.Append(FormatIdentifier(label, functionNames).PadRight(maxLabelWidth));
            sb.Append(' ');
            sb.Append(directive.PadRight(maxDirectiveWidth));
            sb.Append(' ');
            sb.Append(value);
            sb.AppendLine();
        }

        index = rowIndex;
        return true;
    }

    private static string FormatDataDirective(AsmDataDirective directive)
    {
        return directive switch
        {
            AsmDataDirective.Byte => "BYTE",
            AsmDataDirective.Word => "WORD",
            AsmDataDirective.Long => "LONG",
            _ => Assert.UnreachableValue<string>(),
        };
    }

    private static string FormatDataValue(AsmDataNode data, IReadOnlySet<string> functionNames)
    {
        string initializer = data.Initializer switch
        {
            null => "0",
            bool boolean => boolean ? "1" : "0",
            uint u32 when data.UseHexFormat => $"${u32:X8}",
            int i32 when data.UseHexFormat => $"${unchecked((uint)i32):X8}",
            IAsmSymbol symbol => FormatIdentifier(symbol.Name, functionNames),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => data.Initializer.ToString() ?? "0",
        };

        return data.Count > 1 ? $"{initializer}[{data.Count}]" : initializer;
    }

    private static void WriteNode(StringBuilder sb, AsmNode node, IReadOnlySet<string> functionNames)
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

            case AsmSectionNode:
            case AsmDataNode:
                break;

            case AsmLabelNode label:
                sb.Append("  ");
                sb.AppendLine(FormatIdentifier(label.Name, functionNames));
                break;

            case AsmCommentNode comment:
                sb.Append("    ' ");
                sb.AppendLine(comment.Text);
                break;

            case AsmVolatileRegionBeginNode:
            case AsmVolatileRegionEndNode:
                break;


            case AsmInstructionNode instruction:
                sb.Append("    ");
                if (instruction.Condition is P2ConditionCode condition)
                {
                    sb.Append(P2InstructionMetadata.GetConditionPrefixText(condition));
                    sb.Append(' ');
                }

                sb.Append(P2InstructionMetadata.GetMnemonicText(instruction.Mnemonic));
                if (instruction.Operands.Count > 0)
                {
                    sb.Append(' ');
                    for (int i = 0; i < instruction.Operands.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(", ");
                        sb.Append(FormatOperand(instruction, i, functionNames));
                    }
                }

                if (instruction.FlagEffect != P2FlagEffect.None)
                {
                    sb.Append(' ');
                    sb.Append(FormatFlagEffect(instruction.FlagEffect));
                }

                sb.AppendLine();
                break;
        }
    }

    private static string FormatOperand(
        AsmInstructionNode instruction,
        int operandIndex,
        IReadOnlySet<string> functionNames)
    {
        AsmOperand operand = instruction.Operands[operandIndex];

        return operand switch
        {
            AsmPhysicalRegisterOperand phys => phys.Name,
            AsmRegisterOperand virt => virt.Format(),
            AsmImmediateOperand imm => imm.Format(),
            AsmPlaceOperand place => FormatPlaceOperand(place, functionNames),
            AsmSymbolOperand sym => FormatSymbolOperand(sym, instruction, operandIndex, functionNames),
            _ => operand.Format(),
        };
    }

    private static string FormatPlaceOperand(AsmPlaceOperand place, IReadOnlySet<string> functionNames)
    {
        string name = FormatIdentifier(place.Place.EmittedName, functionNames);

        // LUT places live at org $200+ in the unified address space, but
        // RDLUT/WRLUT expect a 9-bit LUT-relative index (0–511).  Emit
        // #label - $200 so the assembler computes the correct offset.
        if (place.Place.StorageClass == VariableStorageClass.Lut)
            return $"#{name} - $200";

        // Hub places live after orgh.  Labels in hub mode already evaluate
        // to hub addresses, so #label gives the correct immediate value.
        if (place.Place.StorageClass == VariableStorageClass.Hub)
            return $"#{name}";

        return name;
    }

    /// <summary>
    /// Format a symbol operand with or without # prefix depending on the operand form.
    /// - Immediate-form operands use #label
    /// - Register/special-register references stay plain
    /// </summary>
    private static string FormatSymbolOperand(
        AsmSymbolOperand sym,
        AsmInstructionNode instruction,
        int operandIndex,
        IReadOnlySet<string> functionNames)
    {
        P2InstructionOperandInfo operandInfo = P2InstructionMetadata.GetOperandInfo(
            instruction.Mnemonic,
            instruction.Operands.Count,
            operandIndex);

        if (sym.AddressingMode == AsmSymbolAddressingMode.Immediate)
        {
            Assert.Invariant(
                operandInfo.SupportsImmediateSyntax,
                $"Instruction '{instruction.Opcode}' operand {operandIndex} does not accept immediate symbols.");
            return $"#{FormatIdentifier(sym.Name, functionNames)}";
        }

        Assert.Invariant(
            !IsImmediateOnlyOperand(operandInfo),
            $"Instruction '{instruction.Opcode}' operand {operandIndex} requires an immediate symbol.");
        return FormatIdentifier(sym.Name, functionNames);
    }

    private static bool IsImmediateOnlyOperand(P2InstructionOperandInfo operandInfo)
        => operandInfo.SupportsImmediateSyntax
            && operandInfo.Access == P2OperandAccess.None
            && operandInfo.Role == P2OperandRole.N;

    private static string FormatFlagEffect(P2FlagEffect effect)
    {
        return effect == P2FlagEffect.None ? string.Empty : effect.ToString();
    }

    private static string FormatIdentifier(string name, IReadOnlySet<string> functionNames)
    {
        if (P2InstructionMetadata.TryParseSpecialRegister(name, out _))
            return name;

        return functionNames.Contains(name)
            ? FormatFunctionIdentifier(name)
            : BackendSymbolNaming.SanitizeIdentifier(name);
    }

    private static string FormatFunctionIdentifier(string name)
    {
        return $"f_{BackendSymbolNaming.SanitizeIdentifier(name)}";
    }
}
