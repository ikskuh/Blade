using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Blade.IR;
using Blade.Reports;
using Blade.Semantics;

using static Blade.Reports.BasicTextSpanKind;
using static Blade.Reports.SemanticTextSpanKind;

namespace Blade.IR.Asm;

/// <summary>
/// Emits final SPIN2 assembly from ASM modules and resolved storage layouts.
/// </summary>
public sealed class FinalAssemblyWriter : TextReportBuilderBase
{
    private const string BladeImageBaseLabel = "blade_image_base";
    private const string BladeEntryLabel = "blade_entry";
    private const string BladeHaltLabel = "blade_halt";
    private sealed class LabelNameEmitter
    {
        private readonly record struct ScopedControlFlowLabelKey(AsmFunctionKey Function, ControlFlowLabelSymbol Label);

        private readonly Dictionary<object, string> emittedNames = [];
        private readonly Dictionary<string, int> usedNames = new(StringComparer.Ordinal);

        public string GetLabelName(IAsmSymbol symbol, AsmFunction? currentFunction = null)
        {
            Requires.NotNull(symbol);

            if (symbol is AsmSpecialRegisterSymbol or AsmCurrentAddressSymbol)
                return symbol.Name;

            object key = GetSymbolKey(symbol, currentFunction);
            if (emittedNames.TryGetValue(key, out string? existingName))
                return existingName;

            string emittedName = AllocateUniqueName(GetBaseName(symbol));
            emittedNames.Add(key, emittedName);
            return emittedName;
        }

        public string GetReservedLabelName(string name)
        {
            Requires.NotNullOrWhiteSpace(name);

            object key = $"reserved:{name}";
            return GetOrCreateName(key, BackendSymbolNaming.SanitizeIdentifier(name));
        }

        public string GetLutVirtualAddressConstantName(StoragePlace place)
        {
            Requires.NotNull(place);
            return GetOrCreateName(("lut-vaddr", (object)place), $"{GetLabelName(place)}_vaddr");
        }

        private string GetOrCreateName(object key, string baseName)
        {
            Requires.NotNull(key);
            Requires.NotNullOrWhiteSpace(baseName);

            if (emittedNames.TryGetValue(key, out string? existingName))
                return existingName;

            string emittedName = AllocateUniqueName(baseName);
            emittedNames.Add(key, emittedName);
            return emittedName;
        }

        private string AllocateUniqueName(string baseName)
        {
            Requires.NotNullOrWhiteSpace(baseName);

            if (usedNames.TryGetValue(baseName, out int seenCount))
            {
                int nextCount = seenCount + 1;
                usedNames[baseName] = nextCount;
                return $"{baseName}_{nextCount}";
            }

            usedNames.Add(baseName, 1);
            return baseName;
        }

        private static object GetSymbolKey(IAsmSymbol symbol, AsmFunction? currentFunction)
        {
            return symbol switch
            {
                StoragePlace place when ShouldCollapseStorageLabel(place) => ("storage", place.EmittedName),
                AsmFunction function => function.Key,
                AsmFunctionReferenceSymbol functionReference => new AsmFunctionKey(functionReference.Image, functionReference.Function),
                ControlFlowLabelSymbol label when currentFunction is not null => new ScopedControlFlowLabelKey(currentFunction.Key, label),
                _ => symbol,
            };
        }

        private static bool ShouldCollapseStorageLabel(StoragePlace place)
        {
            return place.Symbol.IsExtern || place.FixedAddress.HasValue;
        }

        private static string GetBaseName(IAsmSymbol symbol)
        {
            return symbol switch
            {
                StoragePlace place => place.EmittedName,
                AsmFunction function => GetUnscopedFunctionIdentifier(function.Symbol),
                AsmFunctionReferenceSymbol functionReference => GetUnscopedFunctionIdentifier(functionReference.Function),
                _ => BackendSymbolNaming.SanitizeIdentifier(symbol.Name),
            };
        }
    }

    private readonly IReadOnlyList<AsmModule> modules;
    private readonly CogResourceLayoutSet cogResourceLayouts;
    private readonly LabelNameEmitter labelNames = new();

    private FinalAssemblyWriter(ITextReportBuilder builder, IReadOnlyList<AsmModule> modules, CogResourceLayoutSet cogResourceLayouts)
        : base(builder)
    {
        this.modules = Requires.NotNull(modules);
        this.cogResourceLayouts = Requires.NotNull(cogResourceLayouts);
    }

    /// <summary>
    /// Emits the final assembly into the provided report builder.
    /// </summary>
    public static void Write(ITextReportBuilder builder, IReadOnlyList<AsmModule> modules, CogResourceLayoutSet cogResourceLayouts)
    {
        Requires.NotNull(builder);
        Requires.NotNull(modules);
        Requires.NotNull(cogResourceLayouts);

        FinalAssemblyWriter writer = new(builder, modules, cogResourceLayouts);
        writer.WriteAssembly();
    }

    private void WriteAssembly()
    {
        IReadOnlyList<AsmDataBlock> dataBlocks = MergeDataBlocks(this.modules);

        if (HasConSectionContents(dataBlocks))
        {
            AppendLine((Keyword, "CON"));
            WriteConSectionContents(dataBlocks);
            NewLine();
        }

        AppendLine((Keyword, "DAT"));
        AppendLine(Space(4), (Directive, "org"), ' ', (Literal, "0"));
        WriteDatSectionContents(dataBlocks);
    }

    private void WriteConSectionContents(IReadOnlyList<AsmDataBlock> dataBlocks)
    {
        AsmDataBlock? externalBlock = dataBlocks.FirstOrDefault(static block => block.Kind == AsmDataBlockKind.External);
        if (externalBlock is not null)
        {
            foreach (AsmExternalBindingDefinition binding in externalBlock.Definitions.OfType<AsmExternalBindingDefinition>())
            {
                StoragePlace place = binding.Place;
                if (place.SpecialRegisterAlias.HasValue)
                    continue;

                VirtualAddress? virtualAddress = ResolveVirtualAddress(place);
                if (!virtualAddress.HasValue)
                    continue;

                Append(Space(4));
                AppendSymbolLabel(place, currentFunction: null);
                Append(' ', '=', ' ', (Literal, FormatHexLiteral(GetRawAddress(virtualAddress.Value))));
                NewLine();
            }
        }

        AsmDataBlock? lutBlock = dataBlocks.FirstOrDefault(static block => block.Kind == AsmDataBlockKind.Lut);
        if (lutBlock is null)
            return;

        foreach (AsmAllocatedStorageDefinition definition in lutBlock.Definitions.OfType<AsmAllocatedStorageDefinition>())
        {
            if (definition.Symbol is not StoragePlace place)
                continue;

            VirtualAddress? virtualAddress = ResolveVirtualAddress(place);
            if (!virtualAddress.HasValue)
                continue;

            Append(Space(4));
            AppendLutVirtualAddressConstantName(place);
            Append(' ', '=', ' ', (Literal, FormatHexLiteral(GetRawAddress(virtualAddress.Value))));
            NewLine();
        }
    }

    private void WriteDatSectionContents(IReadOnlyList<AsmDataBlock> dataBlocks)
    {
        IReadOnlyList<AsmFunction> functions = this.modules.SelectMany(static module => module.Functions).ToList();

        AppendLine(Space(4), (Comment, "' --- Blade compiler output ---"));

        Dictionary<ImageDescriptor, IReadOnlyList<AsmFunction>> functionsByImage = functions
            .GroupBy(static function => function.OwningImage)
            .ToDictionary(static group => group.Key, static group => (IReadOnlyList<AsmFunction>)group.ToList());
        AsmDataBlock? registerBlock = dataBlocks.FirstOrDefault(static candidate => candidate.Kind == AsmDataBlockKind.Register);
        AsmDataBlock? constantBlock = dataBlocks.FirstOrDefault(static candidate => candidate.Kind == AsmDataBlockKind.Constant);

        foreach (CogResourceLayout imageLayout in this.cogResourceLayouts.Images)
        {
            WriteImageCodeBlock(
                imageLayout,
                functionsByImage.GetValueOrDefault(imageLayout.Image) ?? []);
            WriteImageCogStorageBlocks(imageLayout, registerBlock, constantBlock);
        }

        WriteSharedStorageBlocks(dataBlocks);
    }

    private void WriteImageCodeBlock(CogResourceLayout imageLayout, IReadOnlyList<AsmFunction> functions)
    {
        NewLine();
        AppendLine(Space(4), (Comment, $"' --- image {imageLayout.Image.Task.Name} ({imageLayout.Image.ExecutionMode}) ---"));
        WriteImageCodeOriginDirective(imageLayout);

        foreach (AsmFunction function in functions)
        {
            NewLine();
            AppendLine(Space(4), (Comment, $"' function {function.Name} ({function.CcTier})"));
            if (function.IsEntryPoint && imageLayout.Image.IsEntryImage)
                AppendLine(Space(2), (Literal, this.labelNames.GetReservedLabelName(BladeEntryLabel)));

            Append(Space(2));
            AppendSymbolLabel(function, currentFunction: null);
            NewLine();
            WriteFunctionNodes(function, function.Nodes);
        }

        if (imageLayout.Image.IsEntryImage && functions.Any(static function => function.IsEntryPoint))
        {
            NewLine();
            AppendLine(Space(2), (Literal, this.labelNames.GetReservedLabelName(BladeHaltLabel)));
            AppendLine(Space(4), (Keyword, "NOP"));
            AppendLine(Space(4), (Keyword, "JMP"), ' ', (Literal, "#"), (Literal, this.labelNames.GetReservedLabelName(BladeHaltLabel)));
        }
    }

    private void WriteFunctionNodes(AsmFunction function, IReadOnlyList<AsmNode> nodes)
    {
        foreach (AsmNode node in nodes)
            WriteNode(node, function);
    }

    private void WriteSharedStorageBlocks(IReadOnlyList<AsmDataBlock> dataBlocks)
    {
        AsmDataBlock? hubBlock = dataBlocks.FirstOrDefault(static candidate => candidate.Kind == AsmDataBlockKind.Hub);
        if (hubBlock?.Definitions.OfType<AsmAllocatedStorageDefinition>().Any() != true)
            return;

        NewLine();
        WriteHubStorageBlock(hubBlock, "' --- hub file ---");
    }

    private void WriteImageCogStorageBlocks(CogResourceLayout imageLayout, AsmDataBlock? registerBlock, AsmDataBlock? constantBlock)
    {
        List<AsmAllocatedStorageDefinition> definitions = [];
        if (registerBlock is not null)
        {
            definitions.AddRange(
                registerBlock.Definitions.OfType<AsmAllocatedStorageDefinition>()
                    .Where(definition => SymbolBelongsToImage(definition.Symbol, imageLayout.Image)));
        }

        if (constantBlock is not null)
        {
            definitions.AddRange(
                constantBlock.Definitions.OfType<AsmAllocatedStorageDefinition>()
                    .Where(definition => SymbolBelongsToImage(definition.Symbol, imageLayout.Image)));
        }

        NewLine();
        AppendLine(Space(4), (Comment, "' --- cog data file ---"));

        if (definitions.Count == 0)
        {
            if (imageLayout.Image.ExecutionMode is AddressSpace.Lut or AddressSpace.Hub)
                AppendLine(Space(4), (Directive, "org"), ' ', (Literal, "$0"));
            AppendLine(Space(4), (Directive, "fit"), ' ', (Literal, "$1F0"));
            return;
        }

        definitions = definitions
            .OrderBy(definition => ResolveCogAddress(definition.Symbol))
            .ThenBy(definition => GetLabelName(definition.Symbol), StringComparer.Ordinal)
            .ToList();

        int maxLabelWidth = definitions.Max(definition => GetLabelName(definition.Symbol).Length);
        int maxDirectiveWidth = definitions.Max(static definition => FormatDataDirective(definition.Directive).Length);

        int? previousEndAddress = null;
        foreach (AsmAllocatedStorageDefinition definition in definitions)
        {
            int address = ResolveCogAddress(definition.Symbol);
            if (previousEndAddress != address)
                WriteCogOriginDirective(ResolveCogPhysicalAddressBytes(definition.Symbol), address);

            WriteAllocatedDefinition(definition, maxLabelWidth, maxDirectiveWidth);
            previousEndAddress = address + GetDefinitionSizeInAddressUnits(definition);
        }

        AppendLine(Space(4), (Directive, "fit"), ' ', (Literal, "$1F0"));
    }

    private void WriteHubStorageBlock(AsmDataBlock block, string header)
    {
        List<AsmAllocatedStorageDefinition> placedDefinitions = block.Definitions
            .OfType<AsmAllocatedStorageDefinition>()
            .Where(definition => definition.Symbol is StoragePlace { ResolvedLayoutSlot: not null })
            .OrderBy(static definition => GetRawAddress(((StoragePlace)definition.Symbol).ResolvedLayoutSlot!.Address))
            .ThenBy(definition => definition.Symbol.Name, StringComparer.Ordinal)
            .ToList();
        List<AsmAllocatedStorageDefinition> sequentialDefinitions = block.Definitions
            .OfType<AsmAllocatedStorageDefinition>()
            .Where(definition => definition.Symbol is not StoragePlace { ResolvedLayoutSlot: not null })
            .ToList();

        AppendLine((Comment, header));
        if (placedDefinitions.Count == 0 && sequentialDefinitions.Count == 0)
            return;

        AppendLine(Space(4), (Directive, "orgh"));

        int maxLabelWidth = block.Definitions
            .OfType<AsmAllocatedStorageDefinition>()
            .Select(definition => GetLabelName(definition.Symbol).Length)
            .DefaultIfEmpty(0)
            .Max();
        int maxDirectiveWidth = block.Definitions
            .OfType<AsmAllocatedStorageDefinition>()
            .Select(static definition => FormatDataDirective(definition.Directive).Length)
            .DefaultIfEmpty(0)
            .Max();

        int currentAddress = this.cogResourceLayouts.Images.Count == 0
            ? 0
            : (int)this.cogResourceLayouts.Images[^1].Placement.HubEndAddressExclusive;
        foreach (AsmAllocatedStorageDefinition definition in placedDefinitions)
        {
            StoragePlace place = (StoragePlace)definition.Symbol;
            LayoutSlot slot = Assert.NotNull(place.ResolvedLayoutSlot);
            int slotAddress = GetRawAddress(slot.Address);
            int paddingBytes = slotAddress - currentAddress;
            Assert.Invariant(paddingBytes >= 0, $"Hub storage address for '{place.Symbol.Name}' moved backwards.");
            if (paddingBytes > 0)
            {
                Append(Space(4), (Directive, "BYTE"), ' ', (Literal, "0"), '[', (Literal, paddingBytes.ToString(CultureInfo.InvariantCulture)), ']');
                NewLine();
            }

            WriteAllocatedDefinition(definition, maxLabelWidth, maxDirectiveWidth);
            currentAddress = slotAddress + GetDefinitionSizeInAddressUnits(definition);
        }

        if (sequentialDefinitions.Count == 0)
            return;

        AsmDataBlock sequentialBlock = new(block.Kind, sequentialDefinitions);
        WriteAllocatedBlockContents(sequentialBlock, maxLabelWidth, maxDirectiveWidth, emitAlignmentDirectives: true);
    }

    private void WriteAllocatedBlockContents(AsmDataBlock block, int maxLabelWidth, int maxDirectiveWidth, bool emitAlignmentDirectives)
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
                    AppendLine(Space(4), (Directive, "ALIGNL"));
                else if (currentAlignment == 2)
                    AppendLine(Space(4), (Directive, "ALIGNW"));
            }

            WriteAllocatedDefinition(definition, maxLabelWidth, maxDirectiveWidth);
        }
    }

    private void WriteAllocatedDefinition(AsmAllocatedStorageDefinition definition, int maxLabelWidth, int maxDirectiveWidth)
    {
        string label = GetLabelName(definition.Symbol);
        string directive = FormatDataDirective(definition.Directive);

        AppendSymbolLabel(definition.Symbol, currentFunction: null);
        Append(Space(Math.Max(1, maxLabelWidth - label.Length + 1)));
        Append((Directive, directive));
        Append(Space(Math.Max(1, maxDirectiveWidth - directive.Length + 1)));
        AppendDataValue(definition);
        NewLine();
    }

    private void WriteImageCodeOriginDirective(CogResourceLayout imageLayout)
    {
        if (imageLayout.Image.IsEntryImage)
        {
            AppendLine(Space(4), (Directive, "orgh"));
            AppendLine(Space(2), (Literal, this.labelNames.GetReservedLabelName(BladeImageBaseLabel)));
        }
        else
        {
            Append(Space(4), (Directive, "orgh"), ' ');
            Append((Literal, FormatPhysicalAddressExpression(imageLayout.HubStartAddressBytes)));
            NewLine();
        }

        switch (imageLayout.Image.ExecutionMode)
        {
            case AddressSpace.Cog:
            case AddressSpace.Lut:
                AppendLine(Space(4), (Directive, "org"), ' ', (Literal, "$0"));
                break;

            case AddressSpace.Hub:
                break;

            default:
                Assert.Unreachable($"Unexpected execution mode '{imageLayout.Image.ExecutionMode}'."); // pragma: force-coverage
                break; // pragma: force-coverage
        }
    }

    private void WriteCogOriginDirective(int physicalAddressBytes, int virtualAddress)
    {
        Requires.NonNegative(physicalAddressBytes);
        Requires.InRange(virtualAddress, 0, 0x1FF);

        Append(Space(4), (Directive, "orgh"), ' ');
        Append((Literal, FormatPhysicalAddressExpression(new HubAddress(physicalAddressBytes))));
        NewLine();
        AppendLine(Space(4), (Directive, "org"), ' ', (Literal, FormatHexLiteral(virtualAddress)));
    }

    private void WriteNode(AsmNode node, AsmFunction currentFunction)
    {
        switch (node)
        {
            case AsmLabelNode label:
                Append(Space(2));
                AppendSymbolLabel(label.Label, currentFunction);
                NewLine();
                break;

            case AsmCommentNode comment:
                AppendLine(Space(4), (Comment, "' " + comment.Text));
                break;

            case AsmVolatileRegionBeginNode:
            case AsmVolatileRegionEndNode:
                break;

            case AsmInstructionNode instruction:
                WriteInstructionNode(instruction, currentFunction);
                break;

            case AsmInlineDataNode inlineData:
                WriteInlineDataNode(inlineData, currentFunction);
                break;

            default:
                Assert.Unreachable($"Unhandled ASM node '{node.GetType().Name}'."); // pragma: force-coverage
                break; // pragma: force-coverage
        }
    }

    private void WriteInstructionNode(AsmInstructionNode instruction, AsmFunction currentFunction)
    {
        Append(Space(4));
        if (instruction.Condition is P2ConditionCode condition)
            Append((Keyword, P2MetadataSyntax.GetConditionPrefixText(condition)), ' ');

        Append((Keyword, instruction.Mnemonic.ToString()));
        if (instruction.Operands.Count > 0)
        {
            Append(' ');
            for (int i = 0; i < instruction.Operands.Count; i++)
            {
                if (i > 0)
                    Append(',', ' ');
                AppendOperand(instruction.Operands[i], currentFunction);
            }
        }

        if (instruction.FlagOutput.Effect != P2FlagEffect.None)
            Append(' ', (Keyword, instruction.FlagOutput.Effect.ToString()));

        NewLine();
    }

    private void WriteInlineDataNode(AsmInlineDataNode inlineData, AsmFunction currentFunction)
    {
        Append(Space(4), (Directive, FormatDataDirective(inlineData.Directive)));
        if (inlineData.Values.Count > 0)
        {
            Append(' ');
            for (int i = 0; i < inlineData.Values.Count; i++)
            {
                if (i > 0)
                    Append(',', ' ');
                AppendInlineDataValue(inlineData.Values[i], currentFunction);
            }
        }

        NewLine();
    }

    private void AppendDataValue(AsmAllocatedStorageDefinition definition)
    {
        if (definition.Symbol is AsmSharedConstantSymbol constant)
        {
            AppendSharedConstantValue(constant.Value, definition.UseHexFormat, currentFunction: null);
            return;
        }

        if (definition.InitialValues is null || definition.InitialValues.Count == 0)
        {
            Append((Literal, "0"));
            if (definition.Count > 1)
                Append('[', (Literal, definition.Count.ToString(CultureInfo.InvariantCulture)), ']');
            return;
        }

        if (definition.InitialValues.Count == 1)
        {
            AppendDataOperand(definition.InitialValues[0], definition.UseHexFormat, currentFunction: null);
            if (definition.Count > 1)
                Append('[', (Literal, definition.Count.ToString(CultureInfo.InvariantCulture)), ']');
            return;
        }

        for (int i = 0; i < definition.InitialValues.Count; i++)
        {
            if (i > 0)
                Append(',', ' ');
            AppendDataOperand(definition.InitialValues[i], definition.UseHexFormat, currentFunction: null);
        }
    }

    private void AppendDataOperand(AsmOperand operand, bool useHexFormat, AsmFunction? currentFunction)
    {
        switch (operand)
        {
            case AsmImmediateOperand { Value: >= 0 } immediate when useHexFormat:
                Append((Literal, $"${immediate.Value:X8}"));
                return;

            case AsmImmediateOperand immediate:
                Append((Literal, immediate.Value.ToString(CultureInfo.InvariantCulture)));
                return;

            case AsmSymbolOperand symbol:
                AppendSymbolOperand(symbol, currentFunction);
                return;

            default:
                Append((Literal, operand.Format()));
                return;
        }
    }

    private void AppendSharedConstantValue(AsmSharedConstantValue value, bool useHexFormat, AsmFunction? currentFunction)
    {
        switch (value)
        {
            case AsmLiteralSharedConstantValue literal when useHexFormat:
                Append((Literal, $"${literal.Value:X8}"));
                return;

            case AsmLiteralSharedConstantValue literal:
                Append((Literal, unchecked((int)literal.Value).ToString(CultureInfo.InvariantCulture)));
                return;

            case AsmSymbolSharedConstantValue symbolic:
                AppendOffsetExpression(
                    () => AppendSymbolExpression(symbolic.Symbol, currentFunction, useLutVirtualAddressAlias: false),
                    symbolic.Offset);
                return;

            case AsmLutVirtualAddressSharedConstantValue lut:
                AppendOffsetExpression(
                    () => AppendSymbolExpression(lut.Place, currentFunction, useLutVirtualAddressAlias: true),
                    lut.Offset);
                return;

            default:
                Assert.Unreachable($"Unhandled shared constant value '{value.GetType().Name}'."); // pragma: force-coverage
                return; // pragma: force-coverage
        }
    }

    private void AppendInlineDataValue(AsmInlineDataValue value, AsmFunction currentFunction)
    {
        switch (value)
        {
            case AsmInlineDataOperandValue operandValue when operandValue.PreserveImmediateSyntax:
                AppendInlineDataImmediateOperand(operandValue.Operand, currentFunction);
                return;

            case AsmInlineDataOperandValue operandValue:
                AppendInlineDataDirectOperand(operandValue.Operand, currentFunction);
                return;

            case AsmInlineDataRawSymbolValue raw when raw.PreserveImmediateSyntax:
                Append((Literal, "#" + raw.Name));
                return;

            case AsmInlineDataRawSymbolValue raw:
                Append((Literal, raw.Name));
                return;

            default:
                Assert.Unreachable($"Unhandled inline data value '{value.GetType().Name}'."); // pragma: force-coverage
                return; // pragma: force-coverage
        }
    }

    private void AppendInlineDataImmediateOperand(AsmOperand operand, AsmFunction currentFunction)
    {
        switch (operand)
        {
            case AsmImmediateOperand immediate:
                Append((Literal, "#" + immediate.Value.ToString(CultureInfo.InvariantCulture)));
                return;

            case AsmSymbolOperand symbol:
                Append((Literal, "#"));
                AppendSymbolLabel(symbol.Symbol, currentFunction);
                return;

            default:
                AppendOperand(operand, currentFunction);
                return;
        }
    }

    private void AppendInlineDataDirectOperand(AsmOperand operand, AsmFunction currentFunction)
    {
        switch (operand)
        {
            case AsmImmediateOperand immediate:
                Append((Literal, immediate.Value.ToString(CultureInfo.InvariantCulture)));
                return;

            case AsmSymbolOperand symbol:
                AppendSymbolLabel(symbol.Symbol, currentFunction);
                return;

            default:
                AppendOperand(operand, currentFunction);
                return;
        }
    }

    private void AppendOperand(AsmOperand operand, AsmFunction currentFunction)
    {
        switch (operand)
        {
            case AsmPhysicalRegisterOperand physical:
                Append((Literal, physical.Name));
                return;

            case AsmRegisterOperand register:
                Append((VariableName, register.Value, register.Format()));
                return;

            case AsmImmediateOperand immediate:
                Append((Literal, immediate.Format()));
                return;

            case AsmAltPlaceholderOperand { Kind: AltPlaceholderKind.Immediate }:
                Append((Literal, "#0"));
                return;

            case AsmAltPlaceholderOperand { Kind: AltPlaceholderKind.Register }:
                Append((Literal, "0"));
                return;

            case AsmSymbolOperand symbol:
                AppendSymbolOperand(symbol, currentFunction);
                return;

            case AsmLabelRefOperand labelRef:
                Append('@');
                AppendSymbolLabel(labelRef.Label, currentFunction);
                return;

            default:
                Append((Literal, operand.Format()));
                return;
        }
    }

    private void AppendSymbolOperand(AsmSymbolOperand operand, AsmFunction? currentFunction)
    {
        if (operand.AddressingMode == AsmSymbolAddressingMode.Immediate)
        {
            AppendImmediateOffsetExpression(
                () => AppendSymbolExpression(
                    operand.Symbol,
                    currentFunction,
                    useLutVirtualAddressAlias: operand.Symbol.SymbolType == SymbolType.LutVariable),
                operand.Offset);
            return;
        }

        AppendOffsetExpression(
            () => AppendSymbolExpression(operand.Symbol, currentFunction, useLutVirtualAddressAlias: false),
            operand.Offset);
    }

    private void AppendImmediateOffsetExpression(Action appendBaseExpression, int offset)
    {
        Requires.NotNull(appendBaseExpression);

        if (offset == 0)
        {
            Append((Literal, "#"));
            appendBaseExpression();
            return;
        }

        Append((Literal, "#("));
        appendBaseExpression();
        AppendOffsetSuffix(offset);
        Append(')');
    }

    private void AppendOffsetExpression(Action appendBaseExpression, int offset)
    {
        Requires.NotNull(appendBaseExpression);

        appendBaseExpression();
        AppendOffsetSuffix(offset);
    }

    private void AppendOffsetSuffix(int offset)
    {
        if (offset == 0)
            return;

        if (offset > 0)
            Append(' ', '+', ' ', (Literal, offset.ToString(CultureInfo.InvariantCulture)));
        else
            Append(' ', '-', ' ', (Literal, (-offset).ToString(CultureInfo.InvariantCulture)));
    }

    private void AppendSymbolExpression(IAsmSymbol symbol, AsmFunction? currentFunction, bool useLutVirtualAddressAlias)
    {
        Requires.NotNull(symbol);

        if (symbol is AsmImageStartSymbol imageStart)
        {
            bool found = this.cogResourceLayouts.TryGetImageStartAddress(imageStart.Image, out HubAddress addressBytes);
            Assert.Invariant(found, $"Missing image start address for task '{imageStart.Image.Task.Name}'.");
            Append((Literal, FormatPhysicalAddressExpression(addressBytes)));
            return;
        }

        if (useLutVirtualAddressAlias)
        {
            AppendLutVirtualAddressConstantName(symbol, currentFunction);
            return;
        }

        AppendSymbolLabel(symbol, currentFunction);
    }

    private void AppendLutVirtualAddressConstantName(StoragePlace place)
    {
        Append((VariableName, place, this.labelNames.GetLutVirtualAddressConstantName(place)));
    }

    private void AppendLutVirtualAddressConstantName(IAsmSymbol symbol, AsmFunction? currentFunction)
    {
        if (symbol is StoragePlace place)
        {
            AppendLutVirtualAddressConstantName(place);
            return;
        }

        AppendSymbolLabel(symbol, currentFunction);
        Append((Literal, "_vaddr"));
    }

    private void AppendSymbolLabel(IAsmSymbol symbol, AsmFunction? currentFunction)
    {
        Requires.NotNull(symbol);

        if (symbol is ControlFlowLabelSymbol { Name: BladeHaltLabel })
        {
            Append((Literal, this.labelNames.GetReservedLabelName(BladeHaltLabel)));
            return;
        }

        string text = GetLabelName(symbol, currentFunction);
        switch (symbol)
        {
            case AsmFunction function:
                Append((FunctionName, function, text));
                return;

            case AsmFunctionReferenceSymbol functionReference:
                Append((FunctionName, functionReference, text));
                return;

            case StoragePlace place:
                Append((VariableName, place, text));
                return;

            case ControlFlowLabelSymbol label:
                Append((VariableName, label, text));
                return;

            case AsmSharedConstantSymbol constant:
                Append((VariableName, constant, text));
                return;

            case AsmSpillSlotSymbol spillSlot:
                Append((VariableName, spillSlot, text));
                return;

            default:
                Append((Literal, text));
                return;
        }
    }

    private VirtualAddress? ResolveVirtualAddress(StoragePlace place)
    {
        VirtualAddress? virtualAddress = place.ResolvedLayoutSlot?.Address ?? place.FixedAddress;
        if (!virtualAddress.HasValue
            && this.cogResourceLayouts.TryGetAddress(place, out MemoryAddress memoryAddress))
        {
            virtualAddress = memoryAddress.Virtual;
        }

        return virtualAddress;
    }

    private int ResolveCogAddress(IAsmSymbol symbol)
    {
        Requires.NotNull(symbol);

        return symbol switch
        {
            StoragePlace { ResolvedLayoutSlot: LayoutSlot { StorageClass: AddressSpace.Cog } slot } => GetRawAddress(slot.Address),
            StoragePlace place when this.cogResourceLayouts.TryGetAddress(place, out MemoryAddress stableAddress) => GetRawAddress(stableAddress.Virtual),
            AsmSpillSlotSymbol spillSlot => (int)spillSlot.Slot,
            AsmSharedConstantSymbol constant when this.cogResourceLayouts.TryGetAddress(constant, out MemoryAddress constantAddress) => GetRawAddress(constantAddress.Virtual),
            _ => Assert.UnreachableValue<int>($"Missing COG address for symbol '{symbol.Name}'."), // pragma: force-coverage
        };
    }

    private int ResolveCogPhysicalAddressBytes(IAsmSymbol symbol)
    {
        if (this.cogResourceLayouts.TryGetAddress(symbol, out MemoryAddress address))
            return (int)address.Physical;

        if (TryGetOwningImage(symbol, out ImageDescriptor? image)
            && this.cogResourceLayouts.TryGetImageStartAddress(Assert.NotNull(image), out HubAddress owningImageStart)
            && this.cogResourceLayouts.TryGetAddress(symbol, out MemoryAddress virtualAddress))
        {
            return checked((int)owningImageStart + (GetRawAddress(virtualAddress.Virtual) * 4));
        }

        if (this.cogResourceLayouts.Images.Count == 1
            && this.cogResourceLayouts.TryGetAddress(symbol, out MemoryAddress singleImageVirtualAddress))
        {
            return checked((int)this.cogResourceLayouts.EntryImage.HubStartAddressBytes + (GetRawAddress(singleImageVirtualAddress.Virtual) * 4));
        }

        if (symbol is AsmSpillSlotSymbol spillSlot
            && this.cogResourceLayouts.TryGetImageStartAddress(spillSlot.Image, out HubAddress spillImageStart))
        {
            return checked((int)spillImageStart + ((int)spillSlot.Slot * 4));
        }

        return Assert.UnreachableValue<int>($"Missing physical hub address for symbol '{symbol.Name}'."); // pragma: force-coverage
    }

    private string GetLabelName(IAsmSymbol symbol, AsmFunction? currentFunction = null)
    {
        return this.labelNames.GetLabelName(symbol, currentFunction);
    }

    private string FormatPhysicalAddressExpression(HubAddress addressBytes)
    {
        int rawAddressBytes = (int)addressBytes;
        Requires.NonNegative(rawAddressBytes);
        if (rawAddressBytes == 0)
            return this.labelNames.GetReservedLabelName(BladeImageBaseLabel);

        return $"{this.labelNames.GetReservedLabelName(BladeImageBaseLabel)} + ${rawAddressBytes:X}";
    }

    private static bool HasConSectionContents(IReadOnlyList<AsmDataBlock> dataBlocks)
    {
        AsmDataBlock? externalBlock = dataBlocks.FirstOrDefault(static block => block.Kind == AsmDataBlockKind.External);
        if (externalBlock?.Definitions.OfType<AsmExternalBindingDefinition>().Any() == true)
            return true;

        AsmDataBlock? lutBlock = dataBlocks.FirstOrDefault(static block => block.Kind == AsmDataBlockKind.Lut);
        return lutBlock?.Definitions.OfType<AsmAllocatedStorageDefinition>().Any() == true;
    }

    private static IReadOnlyList<AsmDataBlock> MergeDataBlocks(IReadOnlyList<AsmModule> modules)
    {
        List<AsmDataBlock> blocks = [];
        foreach (AsmDataBlockKind kind in new[]
                 {
                     AsmDataBlockKind.Register,
                     AsmDataBlockKind.Constant,
                     AsmDataBlockKind.Lut,
                     AsmDataBlockKind.External,
                     AsmDataBlockKind.Hub,
                 })
        {
            List<AsmDataDefinition> definitions = [];
            HashSet<object> seen = [];
            foreach (AsmModule module in modules)
            {
                foreach (AsmDataDefinition definition in module.DataBlocks.Where(block => block.Kind == kind).SelectMany(block => block.Definitions))
                {
                    if (!seen.Add(GetDataDefinitionKey(definition)))
                        continue;

                    definitions.Add(definition);
                }
            }

            blocks.Add(new AsmDataBlock(kind, definitions));
        }

        return blocks;
    }

    private static object GetDataDefinitionKey(AsmDataDefinition definition)
    {
        return definition.Symbol switch
        {
            StoragePlace place => (place.Symbol, place.OwningImage),
            AsmSharedConstantSymbol constant => (constant.Image, constant.Value),
            AsmSpillSlotSymbol spill => (spill.Image, spill.Slot),
            _ => definition.Symbol,
        };
    }

    private static int GetDefinitionSizeInAddressUnits(AsmAllocatedStorageDefinition definition)
    {
        Requires.NotNull(definition);

        if (definition.StorageClass is AddressSpace.Cog or AddressSpace.Lut)
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

    private static string FormatHexLiteral(int value)
    {
        return "$" + value.ToString("X", CultureInfo.InvariantCulture);
    }

    private static string GetUnscopedFunctionIdentifier(FunctionSymbol function)
    {
        Requires.NotNull(function);
        return $"f_{BackendSymbolNaming.SanitizeIdentifier(function.Name)}";
    }

    private static bool SymbolBelongsToImage(IAsmSymbol symbol, ImageDescriptor image)
    {
        return symbol switch
        {
            StoragePlace place => ReferenceEquals(place.OwningImage, image),
            AsmSharedConstantSymbol constant => ReferenceEquals(constant.Image, image),
            AsmSpillSlotSymbol spill => ReferenceEquals(spill.Image, image),
            _ => false,
        };
    }

    private static int GetRawAddress(VirtualAddress address)
    {
        (_, int rawAddress) = address.GetDataAddress();
        return rawAddress;
    }

    private static bool TryGetOwningImage(IAsmSymbol symbol, out ImageDescriptor? image)
    {
        image = symbol switch
        {
            StoragePlace place => place.OwningImage,
            AsmSharedConstantSymbol constant => constant.Image,
            AsmSpillSlotSymbol spill => spill.Image,
            AsmImageStartSymbol imageStart => imageStart.Image,
            _ => null,
        };

        return image is not null;
    }
}
