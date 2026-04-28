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
    public static string Write(AsmModule module, CogResourceLayoutSet cogResourceLayouts)
    {
        return Build(module, cogResourceLayouts).Text;
    }

    public static FinalAssembly Build(AsmModule module, CogResourceLayoutSet cogResourceLayouts, RuntimeTemplate? runtimeTemplate = null)
    {
        Requires.NotNull(module);
        Requires.NotNull(cogResourceLayouts);

        string conSectionContents = WriteConSectionContents(module);
        string datSectionContents = WriteDatSectionContents(module, cogResourceLayouts, includeDefaultBladeHalt: runtimeTemplate is null);
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
            if (place.FixedAddress is not int fixedAddress || place.SpecialRegisterAlias.HasValue)
                continue;

            sb.Append("    ");
            sb.Append(FormatSymbol(place));
            sb.Append(" = $");
            sb.Append(fixedAddress.ToString("X", CultureInfo.InvariantCulture));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string WriteDatSectionContents(AsmModule module, CogResourceLayoutSet cogResourceLayouts)
    {
        return WriteDatSectionContents(module, cogResourceLayouts, includeDefaultBladeHalt: false);
    }

    private static string WriteDatSectionContents(AsmModule module, CogResourceLayoutSet cogResourceLayouts, bool includeDefaultBladeHalt)
    {
        Requires.NotNull(module);
        Requires.NotNull(cogResourceLayouts);

        IReadOnlyDictionary<FunctionSymbol, string> functionIdentifiers = BuildFunctionIdentifiers(module.Functions);
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
            sb.AppendLine(FormatFunctionIdentifier(functionIdentifiers, function.Symbol));
            WriteFunctionNodes(sb, function.Nodes, functionIdentifiers);
            if (includeDefaultBladeHalt && function.IsEntryPoint)
                WriteDefaultBladeHalt(sb);
        }

        WriteDataBlocks(sb, module.DataBlocks, functionIdentifiers, cogResourceLayouts);
        return sb.ToString();
    }

    private static IReadOnlyDictionary<FunctionSymbol, string> BuildFunctionIdentifiers(IReadOnlyList<AsmFunction> functions)
    {
        Dictionary<string, int> emittedNameCounts = new(StringComparer.Ordinal);
        Dictionary<FunctionSymbol, string> identifiers = [];
        foreach (AsmFunction function in functions)
        {
            string baseName = $"f_{BackendSymbolNaming.SanitizeIdentifier(function.Name)}";
            if (emittedNameCounts.TryGetValue(baseName, out int seenCount))
            {
                int nextCount = seenCount + 1;
                emittedNameCounts[baseName] = nextCount;
                identifiers.Add(function.Symbol, $"{baseName}_{nextCount}");
                continue;
            }

            emittedNameCounts.Add(baseName, 1);
            identifiers.Add(function.Symbol, baseName);
        }

        return identifiers;
    }

    private static void WriteFunctionNodes(StringBuilder sb, IReadOnlyList<AsmNode> nodes, IReadOnlyDictionary<FunctionSymbol, string> functionIdentifiers)
    {
        foreach (AsmNode node in nodes)
            WriteNode(sb, node, functionIdentifiers);
    }

    private static void WriteDefaultBladeHalt(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("    ' halt: default runtime hook");
        sb.AppendLine("  blade_halt");
        sb.AppendLine("    REP #1, #0");
        sb.AppendLine("    NOP");
    }

    private static void WriteDataBlocks(
        StringBuilder sb,
        IReadOnlyList<AsmDataBlock> dataBlocks,
        IReadOnlyDictionary<FunctionSymbol, string> functionIdentifiers,
        CogResourceLayoutSet cogResourceLayouts)
    {
        AsmDataBlock? registerBlock = dataBlocks.FirstOrDefault(static candidate => candidate.Kind == AsmDataBlockKind.Register);
        AsmDataBlock? constantBlock = dataBlocks.FirstOrDefault(static candidate => candidate.Kind == AsmDataBlockKind.Constant);
        WriteCogStorageBlocks(sb, registerBlock, constantBlock, functionIdentifiers, cogResourceLayouts);

        AsmDataBlock? lutBlock = dataBlocks.FirstOrDefault(static candidate => candidate.Kind == AsmDataBlockKind.Lut);
        if (lutBlock is not null)
        {
            sb.AppendLine();
            sb.AppendLine("    fit $200");
            WriteStorageBlock(sb, lutBlock, "' --- lut file ---", functionIdentifiers, VariableStorageClass.Lut);
        }

        AsmDataBlock? hubBlock = dataBlocks.FirstOrDefault(static candidate => candidate.Kind == AsmDataBlockKind.Hub);
        if (hubBlock is not null)
        {
            sb.AppendLine();
            WriteStorageBlock(sb, hubBlock, "' --- hub file ---", functionIdentifiers, VariableStorageClass.Hub);
        }
    }

    private static void WriteCogStorageBlocks(
        StringBuilder sb,
        AsmDataBlock? registerBlock,
        AsmDataBlock? constantBlock,
        IReadOnlyDictionary<FunctionSymbol, string> functionIdentifiers,
        CogResourceLayoutSet cogResourceLayouts)
    {
        List<AsmAllocatedStorageDefinition> definitions = [];
        if (registerBlock is not null)
            definitions.AddRange(registerBlock.Definitions.OfType<AsmAllocatedStorageDefinition>());
        if (constantBlock is not null)
            definitions.AddRange(constantBlock.Definitions.OfType<AsmAllocatedStorageDefinition>());

        sb.AppendLine();
        sb.AppendLine("    ' --- cog data file ---");

        if (definitions.Count == 0)
        {
            sb.AppendLine("    fit $1F0");
            return;
        }

        definitions = definitions
            .OrderBy(definition => ResolveCogAddress(definition.Symbol, cogResourceLayouts))
            .ThenBy(definition => FormatSymbol(definition.Symbol, functionIdentifiers), StringComparer.Ordinal)
            .ToList();

        int maxLabelWidth = definitions.Max(definition => FormatSymbol(definition.Symbol, functionIdentifiers).Length);
        int maxDirectiveWidth = definitions.Max(static definition => FormatDataDirective(definition.Directive).Length);

        int? previousEndAddress = null;
        foreach (AsmAllocatedStorageDefinition definition in definitions)
        {
            int address = ResolveCogAddress(definition.Symbol, cogResourceLayouts);
            if (previousEndAddress != address)
                WriteCogOriginDirective(sb, address);

            WriteAllocatedDefinition(sb, definition, functionIdentifiers, maxLabelWidth, maxDirectiveWidth);
            previousEndAddress = address + GetDefinitionSizeInAddressUnits(definition);
        }

        sb.AppendLine("    fit $1F0");
    }

    private static void WriteStorageBlock(
        StringBuilder sb,
        AsmDataBlock block,
        string header,
        IReadOnlyDictionary<FunctionSymbol, string> functionIdentifiers,
        VariableStorageClass storageClass)
    {
        List<AsmAllocatedStorageDefinition> placedDefinitions = block.Definitions
            .OfType<AsmAllocatedStorageDefinition>()
            .Where(definition => definition.Symbol is StoragePlace { ResolvedLayoutSlot: not null })
            .OrderBy(static definition => ((StoragePlace)definition.Symbol).ResolvedLayoutSlot!.Address)
            .ThenBy(definition => definition.Symbol.Name, StringComparer.Ordinal)
            .ToList();
        if (placedDefinitions.Count == 0)
        {
            if (storageClass == VariableStorageClass.Lut)
                sb.AppendLine("    org $200");
            else
                sb.AppendLine("    orgh");

            WriteAllocatedBlock(sb, block, header, functionIdentifiers, emitAlignmentDirectives: storageClass == VariableStorageClass.Hub);
            return;
        }

        List<AsmAllocatedStorageDefinition> sequentialDefinitions = block.Definitions
            .OfType<AsmAllocatedStorageDefinition>()
            .Where(definition => definition.Symbol is not StoragePlace { ResolvedLayoutSlot: not null })
            .ToList();

        sb.AppendLine();
        sb.AppendLine(header);

        int maxLabelWidth = block.Definitions
            .OfType<AsmAllocatedStorageDefinition>()
            .Select(definition => FormatSymbol(definition.Symbol, functionIdentifiers).Length)
            .DefaultIfEmpty(0)
            .Max();
        int maxDirectiveWidth = block.Definitions
            .OfType<AsmAllocatedStorageDefinition>()
            .Select(static definition => FormatDataDirective(definition.Directive).Length)
            .DefaultIfEmpty(0)
            .Max();

        int nextAddress = 0;
        foreach (AsmAllocatedStorageDefinition definition in placedDefinitions)
        {
            StoragePlace place = (StoragePlace)definition.Symbol;
            LayoutSlot slot = Assert.NotNull(place.ResolvedLayoutSlot);
            WriteOriginDirective(sb, storageClass, slot.Address);
            WriteAllocatedDefinition(sb, definition, functionIdentifiers, maxLabelWidth, maxDirectiveWidth);
            nextAddress = Math.Max(nextAddress, slot.Address + GetDefinitionSizeInAddressUnits(definition));
        }

        if (sequentialDefinitions.Count == 0)
            return;

        WriteOriginDirective(sb, storageClass, nextAddress);
        AsmDataBlock sequentialBlock = new(block.Kind, sequentialDefinitions);
        WriteAllocatedBlockContents(
            sb,
            sequentialBlock,
            functionIdentifiers,
            maxLabelWidth,
            maxDirectiveWidth,
            emitAlignmentDirectives: storageClass == VariableStorageClass.Hub);
    }

    private static void WriteAllocatedBlock(
        StringBuilder sb,
        AsmDataBlock block,
        string header,
        IReadOnlyDictionary<FunctionSymbol, string> functionIdentifiers,
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

        int maxLabelWidth = definitions.Max(definition => FormatSymbol(definition.Symbol, functionIdentifiers).Length);
        int maxDirectiveWidth = definitions.Max(static definition => FormatDataDirective(definition.Directive).Length);
        WriteAllocatedBlockContents(sb, block, functionIdentifiers, maxLabelWidth, maxDirectiveWidth, emitAlignmentDirectives);
    }

    private static void WriteAllocatedBlockContents(
        StringBuilder sb,
        AsmDataBlock block,
        IReadOnlyDictionary<FunctionSymbol, string> functionIdentifiers,
        int maxLabelWidth,
        int maxDirectiveWidth,
        bool emitAlignmentDirectives)
    {
        List<AsmAllocatedStorageDefinition> definitions = block.Definitions
            .OfType<AsmAllocatedStorageDefinition>()
            .OrderByDescending(static definition => definition.AlignmentBytes)
            .ThenBy(static definition => definition.Symbol.Name, StringComparer.Ordinal)
            .ToList();
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

            WriteAllocatedDefinition(sb, definition, functionIdentifiers, maxLabelWidth, maxDirectiveWidth);
        }
    }

    private static void WriteAllocatedDefinition(
        StringBuilder sb,
        AsmAllocatedStorageDefinition definition,
        IReadOnlyDictionary<FunctionSymbol, string> functionIdentifiers,
        int maxLabelWidth,
        int maxDirectiveWidth)
    {
        string label = FormatSymbol(definition.Symbol, functionIdentifiers);
        string directive = FormatDataDirective(definition.Directive);
        string value = FormatDataValue(definition, functionIdentifiers);

        sb.Append(label.PadRight(maxLabelWidth));
        sb.Append(' ');
        sb.Append(directive.PadRight(maxDirectiveWidth));
        sb.Append(' ');
        sb.Append(value);
        sb.AppendLine();
    }

    private static void WriteOriginDirective(StringBuilder sb, VariableStorageClass storageClass, int address)
    {
        Requires.NotNull(sb);
        Requires.NonNegative(address);

        switch (storageClass)
        {
            case VariableStorageClass.Lut:
                int lutOrigin = checked(0x200 + address);
                sb.Append("    org $");
                sb.AppendLine(lutOrigin.ToString("X", CultureInfo.InvariantCulture));
                break;

            case VariableStorageClass.Hub:
                if (address == 0)
                {
                    sb.AppendLine("    orgh");
                    break;
                }

                sb.Append("    orgh $");
                sb.AppendLine(address.ToString("X", CultureInfo.InvariantCulture));
                break;

            default:
                Assert.Unreachable($"Unexpected storage origin class '{storageClass}'."); // pragma: force-coverage
                break; // pragma: force-coverage
        }
    }

    private static void WriteCogOriginDirective(StringBuilder sb, int address)
    {
        Requires.NotNull(sb);
        Requires.InRange(address, 0, 0x1FF);

        sb.Append("    orgh $");
        sb.AppendLine(address.ToString("X", CultureInfo.InvariantCulture));
        sb.Append("    org $");
        sb.AppendLine(address.ToString("X", CultureInfo.InvariantCulture));
    }

    private static int ResolveCogAddress(IAsmSymbol symbol, CogResourceLayoutSet cogResourceLayouts)
    {
        Requires.NotNull(symbol);
        Requires.NotNull(cogResourceLayouts);

        return symbol switch
        {
            StoragePlace { ResolvedLayoutSlot: LayoutSlot { StorageClass: VariableStorageClass.Cog } slot } => slot.Address,
            StoragePlace place when cogResourceLayouts.TryGetStableAddress(place, out int stableAddress) => stableAddress,
            AsmSpillSlotSymbol spillSlot => spillSlot.Slot,
            AsmSharedConstantSymbol constant when cogResourceLayouts.TryGetStableAddress(constant, out int constantAddress) => constantAddress,
            _ => Assert.UnreachableValue<int>($"Missing COG address for symbol '{symbol.Name}'."), // pragma: force-coverage
        };
    }

    private static int GetDefinitionSizeInAddressUnits(AsmAllocatedStorageDefinition definition)
    {
        Requires.NotNull(definition);

        if (definition.StorageClass is VariableStorageClass.Cog or VariableStorageClass.Lut)
            return definition.Count;

        return definition.Directive switch
        {
            AsmDataDirective.Byte => definition.Count,
            AsmDataDirective.Word => definition.Count * 2,
            AsmDataDirective.Long => definition.Count * 4,
            _ => Assert.UnreachableValue<int>(), // pragma: force-coverage
        };
    }

    private static string FormatDataDirective(AsmDataDirective directive)
    {
        return directive switch
        {
            AsmDataDirective.Byte => "BYTE",
            AsmDataDirective.Word => "WORD",
            AsmDataDirective.Long => "LONG",
            _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
        };
    }

    private static string FormatDataValue(AsmAllocatedStorageDefinition definition, IReadOnlyDictionary<FunctionSymbol, string> functionIdentifiers)
    {
        if (definition.InitialValues is null || definition.InitialValues.Count == 0)
            return definition.Count > 1 ? $"0[{definition.Count}]" : "0";

        if (definition.InitialValues.Count == 1)
        {
            string initializer = FormatDataOperand(definition.InitialValues[0], definition.UseHexFormat, functionIdentifiers);
            return definition.Count > 1 ? $"{initializer}[{definition.Count}]" : initializer;
        }

        List<string> values = new(definition.InitialValues.Count);
        foreach (AsmOperand operand in definition.InitialValues)
            values.Add(FormatDataOperand(operand, definition.UseHexFormat, functionIdentifiers));
        return string.Join(", ", values);
    }

    private static string FormatDataOperand(AsmOperand operand, bool useHexFormat, IReadOnlyDictionary<FunctionSymbol, string> functionIdentifiers)
    {
        return operand switch
        {
            AsmImmediateOperand { Value: >= 0 } immediate when useHexFormat => $"${immediate.Value:X8}",
            AsmImmediateOperand immediate => immediate.Value.ToString(CultureInfo.InvariantCulture),
            AsmSymbolOperand symbol => FormatSymbolOperand(symbol, functionIdentifiers).TrimStart('#'),
            _ => operand.Format(),
        };
    }

    private static void WriteNode(StringBuilder sb, AsmNode node, IReadOnlyDictionary<FunctionSymbol, string> functionIdentifiers)
    {
        switch (node)
        {
            case AsmLabelNode label:
                sb.Append("  ");
                sb.AppendLine(FormatSymbol(label.Label, functionIdentifiers));
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
                        sb.Append(FormatOperand(instruction, i, functionIdentifiers));
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

    private static string FormatOperand(AsmInstructionNode instruction, int operandIndex, IReadOnlyDictionary<FunctionSymbol, string> functionIdentifiers)
    {
        AsmOperand operand = instruction.Operands[operandIndex];
        return operand switch
        {
            AsmPhysicalRegisterOperand physical => physical.Name,
            AsmRegisterOperand register => register.Format(),
            AsmImmediateOperand immediate => immediate.Format(),
            AsmAltPlaceholderOperand { Kind: AltPlaceholderKind.Immediate } => "#0",
            AsmAltPlaceholderOperand { Kind: AltPlaceholderKind.Register } => "0",
            AsmSymbolOperand symbol => FormatSymbolOperand(symbol, functionIdentifiers),
            AsmLabelRefOperand labelRef => labelRef.Format(),
            _ => operand.Format(),
        };
    }

    private static string FormatSymbolOperand(AsmSymbolOperand operand, IReadOnlyDictionary<FunctionSymbol, string> functionIdentifiers)
    {
        if (operand.Symbol is StoragePlace { StorageClass: VariableStorageClass.Lut } lutPlace
            && operand.AddressingMode == AsmSymbolAddressingMode.Immediate)
        {
            string lutBase = $"{FormatSymbol(lutPlace, functionIdentifiers)} - $200";
            return FormatImmediateOffsetExpression(lutBase, operand.Offset, useLongImmediate: true);
        }

        string formatted = FormatSymbol(operand.Symbol, functionIdentifiers);
        if (operand.AddressingMode == AsmSymbolAddressingMode.Immediate)
        {
            bool useLongImmediate = operand.Symbol is StoragePlace;
            return FormatImmediateOffsetExpression(formatted, operand.Offset, useLongImmediate);
        }

        if (operand.Offset != 0)
            formatted = operand.Offset > 0 ? $"{formatted} + {operand.Offset}" : $"{formatted} - {-operand.Offset}";

        return formatted;
    }

    private static string FormatImmediateOffsetExpression(string baseExpression, int offset, bool useLongImmediate)
    {
        string prefix = useLongImmediate ? "##" : "#";

        if (offset == 0)
            return $"{prefix}{baseExpression}";

        string offsetExpression = offset > 0
            ? $"{baseExpression} + {offset}"
            : $"{baseExpression} - {-offset}";
        return $"{prefix}({offsetExpression})";
    }

    private static string FormatSymbol(IAsmSymbol symbol, IReadOnlyDictionary<FunctionSymbol, string> functionIdentifiers)
    {
        return symbol switch
        {
            AsmSpecialRegisterSymbol => symbol.Name,
            AsmCurrentAddressSymbol => symbol.Name,
            AsmFunction function => FormatFunctionIdentifier(functionIdentifiers, function.Symbol),
            AsmFunctionReferenceSymbol functionReference => FormatFunctionIdentifier(functionIdentifiers, functionReference.Function),
            _ => BackendSymbolNaming.SanitizeIdentifier(symbol.Name),
        };
    }

    private static string FormatSymbol(IAsmSymbol symbol)
    {
        return symbol switch
        {
            AsmSpecialRegisterSymbol => symbol.Name,
            AsmCurrentAddressSymbol => symbol.Name,
            _ => BackendSymbolNaming.SanitizeIdentifier(symbol.Name),
        };
    }

    private static string FormatFunctionIdentifier(IReadOnlyDictionary<FunctionSymbol, string> functionIdentifiers, FunctionSymbol function)
    {
        return functionIdentifiers[function];
    }
}
