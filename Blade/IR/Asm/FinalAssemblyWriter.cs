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
    public static string Write(AsmModule module)
    {
        return Build(module).Text;
    }

    public static FinalAssembly Build(AsmModule module, RuntimeTemplate? runtimeTemplate = null)
    {
        Requires.NotNull(module);

        string conSectionContents = WriteConSectionContents(module);
        string datSectionContents = WriteDatSectionContents(module, includeDefaultBladeHalt: runtimeTemplate is null);
        return FinalAssemblyComposer.Compose(conSectionContents, datSectionContents, runtimeTemplate);
    }

    public static string WriteConSectionContents(AsmModule module)
    {
        Requires.NotNull(module);

        StringBuilder sb = new();
        AsmDataBlock? externalBlock = module.DataBlocks.FirstOrDefault(static block => block.Kind == AsmDataBlockKind.External);
        if (externalBlock is null)
            return string.Empty;

        foreach (AsmExternalBindingDefinition binding in externalBlock.Definitions.OfType<AsmExternalBindingDefinition>())
        {
            StoragePlace place = binding.Place;
            if (place.FixedAddress is not int fixedAddress || place.SpecialRegisterAlias is not null)
                continue;

            sb.Append("    ");
            sb.Append(FormatSymbol(place));
            sb.Append(" = $");
            sb.Append(fixedAddress.ToString("X", CultureInfo.InvariantCulture));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string WriteDatSectionContents(AsmModule module)
    {
        return WriteDatSectionContents(module, includeDefaultBladeHalt: false);
    }

    private static string WriteDatSectionContents(AsmModule module, bool includeDefaultBladeHalt)
    {
        Requires.NotNull(module);

        StringBuilder sb = new();
        sb.AppendLine("    ' --- Blade compiler output ---");

        foreach (AsmFunction function in module.Functions)
        {
            sb.AppendLine();
            sb.Append("    ' function ");
            sb.Append(function.Name);
            sb.Append(" (");
            sb.Append(function.CcTier);
            sb.AppendLine(")");
            if (function.IsEntryPoint)
                sb.AppendLine("  blade_entry");
            sb.Append("  ");
            sb.AppendLine(FormatFunctionIdentifier(function.Name));
            WriteFunctionNodes(sb, function.Nodes);
            if (includeDefaultBladeHalt && function.IsEntryPoint)
                WriteDefaultBladeHalt(sb);
        }

        WriteDataBlocks(sb, module.DataBlocks);
        return sb.ToString();
    }

    private static void WriteFunctionNodes(StringBuilder sb, IReadOnlyList<AsmNode> nodes)
    {
        foreach (AsmNode node in nodes)
            WriteNode(sb, node);
    }

    private static void WriteDefaultBladeHalt(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("    ' halt: default runtime hook");
        sb.AppendLine("  blade_halt");
        sb.AppendLine("    REP #1, #0");
        sb.AppendLine("    NOP");
    }

    private static void WriteDataBlocks(StringBuilder sb, IReadOnlyList<AsmDataBlock> dataBlocks)
    {
        foreach (AsmDataBlockKind kind in new[] { AsmDataBlockKind.Register, AsmDataBlockKind.Constant, AsmDataBlockKind.Lut, AsmDataBlockKind.External, AsmDataBlockKind.Hub })
        {
            AsmDataBlock? block = dataBlocks.FirstOrDefault(candidate => candidate.Kind == kind);
            if (block is null)
                continue;

            switch (kind)
            {
                case AsmDataBlockKind.Register:
                    WriteAllocatedBlock(sb, block, "' --- register file ---");
                    break;
                case AsmDataBlockKind.Constant:
                    WriteAllocatedBlock(sb, block, "' --- constant file ---");
                    break;
                case AsmDataBlockKind.Lut:
                    sb.AppendLine();
                    sb.AppendLine("    fit $200");
                    sb.AppendLine("    org $200");
                    WriteAllocatedBlock(sb, block, "' --- lut file ---");
                    break;
                case AsmDataBlockKind.External:
                    break;
                case AsmDataBlockKind.Hub:
                    sb.AppendLine();
                    sb.AppendLine("    orgh");
                    WriteAllocatedBlock(sb, block, "' --- hub file ---", emitAlignmentDirectives: true);
                    break;
            }
        }
    }

    private static void WriteAllocatedBlock(
        StringBuilder sb,
        AsmDataBlock block,
        string header,
        bool emitAlignmentDirectives = false)
    {
        List<AsmAllocatedStorageDefinition> definitions = block.Definitions
            .OfType<AsmAllocatedStorageDefinition>()
            .OrderByDescending(static definition => definition.AlignmentBytes)
            .ThenBy(static definition => definition.Symbol.Name, StringComparer.Ordinal)
            .ToList();

        sb.AppendLine();
        sb.AppendLine(header);
        if (definitions.Count == 0)
            return;

        int maxLabelWidth = definitions.Max(static definition => FormatSymbol(definition.Symbol).Length);
        int maxDirectiveWidth = definitions.Max(static definition => FormatDataDirective(definition.Directive).Length);
        int currentAlignment = -1;

        foreach (AsmAllocatedStorageDefinition definition in definitions)
        {
            if (emitAlignmentDirectives && definition.AlignmentBytes != currentAlignment)
            {
                currentAlignment = definition.AlignmentBytes;
                if (currentAlignment >= 4)
                    sb.AppendLine("    ALIGNL");
                else if (currentAlignment == 2)
                    sb.AppendLine("    ALIGNW");
            }

            string label = FormatSymbol(definition.Symbol);
            string directive = FormatDataDirective(definition.Directive);
            string value = FormatDataValue(definition);

            sb.Append(label.PadRight(maxLabelWidth));
            sb.Append(' ');
            sb.Append(directive.PadRight(maxDirectiveWidth));
            sb.Append(' ');
            sb.Append(value);
            sb.AppendLine();
        }
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

    private static string FormatDataValue(AsmAllocatedStorageDefinition definition)
    {
        object? initialValue = definition.InitialValue?.Value;
        string initializer = initialValue switch
        {
            null => "0",
            bool boolean => boolean ? "1" : "0",
            uint u32 when definition.UseHexFormat => $"${u32:X8}",
            int i32 when definition.UseHexFormat => $"${unchecked((uint)i32):X8}",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => initialValue.ToString()!,
        };

        return definition.Count > 1 ? $"{initializer}[{definition.Count}]" : initializer;
    }

    private static void WriteNode(StringBuilder sb, AsmNode node)
    {
        switch (node)
        {
            case AsmLabelNode label:
                sb.Append("  ");
                sb.AppendLine(FormatSymbol(label.Label));
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
                        sb.Append(FormatOperand(instruction, i));
                    }
                }

                if (instruction.FlagEffect != P2FlagEffect.None)
                {
                    sb.Append(' ');
                    sb.Append(instruction.FlagEffect);
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
            AsmPhysicalRegisterOperand physical => physical.Name,
            AsmRegisterOperand register => register.Format(),
            AsmImmediateOperand immediate => immediate.Format(),
            AsmAltPlaceholderOperand { Kind: AltPlaceholderKind.Immediate } => "#0",
            AsmAltPlaceholderOperand { Kind: AltPlaceholderKind.Register } => "0",
            AsmSymbolOperand symbol => FormatSymbolOperand(symbol),
            AsmLabelRefOperand labelRef => labelRef.Format(),
            _ => operand.Format(),
        };
    }

    private static string FormatSymbolOperand(AsmSymbolOperand operand)
    {
        if (operand.Symbol is StoragePlace { StorageClass: VariableStorageClass.Lut } lutPlace
            && operand.AddressingMode == AsmSymbolAddressingMode.Immediate)
        {
            return $"#{FormatSymbol(lutPlace)} - $200";
        }

        string formatted = FormatSymbol(operand.Symbol);
        return operand.AddressingMode == AsmSymbolAddressingMode.Immediate
            ? $"#{formatted}"
            : formatted;
    }

    private static string FormatSymbol(IAsmSymbol symbol)
    {
        return symbol switch
        {
            AsmSpecialRegisterSymbol => symbol.Name,
            AsmFunction => FormatFunctionIdentifier(symbol.Name),
            AsmFunctionReferenceSymbol => FormatFunctionIdentifier(symbol.Name),
            _ => BackendSymbolNaming.SanitizeIdentifier(symbol.Name),
        };
    }

    private static string FormatFunctionIdentifier(string name)
    {
        return $"f_{BackendSymbolNaming.SanitizeIdentifier(name)}";
    }
}
