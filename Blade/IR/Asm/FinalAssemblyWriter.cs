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
    private const string BladeImageBaseLabel = "blade_image_base";

    public static string Write(AsmModule module, CogResourceLayoutSet cogResourceLayouts)
    {
        return Build(module, cogResourceLayouts).Text;
    }

    public static FinalAssembly Build(AsmModule module, CogResourceLayoutSet cogResourceLayouts)
    {
        Requires.NotNull(module);
        Requires.NotNull(cogResourceLayouts);

        string conSectionContents = WriteConSectionContents(module, cogResourceLayouts);
        string datSectionContents = WriteDatSectionContents(module, cogResourceLayouts, includeDefaultBladeHalt: true);
        return FinalAssemblyComposer.Compose(conSectionContents, datSectionContents);
    }

    public static string WriteConSectionContents(AsmModule module, CogResourceLayoutSet cogResourceLayouts)
    {
        Requires.NotNull(module);
        Requires.NotNull(cogResourceLayouts);

        StringBuilder sb = new();
        AsmDataBlock? externalBlock = module.DataBlocks.FirstOrDefault(static block => block.Kind == AsmDataBlockKind.External);
        if (externalBlock is null)
            return string.Empty;

        foreach (AsmExternalBindingDefinition binding in externalBlock.Definitions.OfType<AsmExternalBindingDefinition>())
        {
            StoragePlace place = binding.Place;
            if (place.SpecialRegisterAlias.HasValue)
                continue;

            VirtualAddress? virtualAddress = place.ResolvedLayoutSlot?.Address ?? place.FixedAddress;
            if (!virtualAddress.HasValue
                && cogResourceLayouts.TryGetAddress(place, out MemoryAddress memoryAddress))
            {
                virtualAddress = memoryAddress.Virtual;
            }

            if (!virtualAddress.HasValue)
                continue;

            sb.Append("    ");
            sb.Append(FormatSymbol(place));
            sb.Append(" = $");
            sb.Append(GetRawAddress(virtualAddress.Value).ToString("X", CultureInfo.InvariantCulture));
            sb.AppendLine();
        }

        AsmDataBlock? lutBlock = module.DataBlocks.FirstOrDefault(static block => block.Kind == AsmDataBlockKind.Lut);
        if (lutBlock is not null)
        {
            foreach (AsmAllocatedStorageDefinition definition in lutBlock.Definitions.OfType<AsmAllocatedStorageDefinition>())
            {
                if (definition.Symbol is not StoragePlace place)
                    continue;

                VirtualAddress? virtualAddress = place.ResolvedLayoutSlot?.Address ?? place.FixedAddress;
                if (!virtualAddress.HasValue)
                    continue;

                sb.Append("    ");
                sb.Append(GetLutVirtualAddressConstantName(place));
                sb.Append(" = $");
                sb.Append(GetRawAddress(virtualAddress.Value).ToString("X", CultureInfo.InvariantCulture));
                sb.AppendLine();
            }
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

        IReadOnlyDictionary<ImageDescriptor, string> imageIdentifiers = BuildImageIdentifiers(cogResourceLayouts.Images);
        IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers = BuildFunctionIdentifiers(module.Functions, imageIdentifiers);
        StringBuilder sb = new();
        sb.AppendLine("    ' --- Blade compiler output ---");

        Dictionary<ImageDescriptor, IReadOnlyList<AsmFunction>> functionsByImage = module.Functions
            .GroupBy(static function => function.OwningImage)
            .ToDictionary(static group => group.Key, static group => (IReadOnlyList<AsmFunction>)group.ToList());
        AsmDataBlock? registerBlock = module.DataBlocks.FirstOrDefault(static candidate => candidate.Kind == AsmDataBlockKind.Register);
        AsmDataBlock? constantBlock = module.DataBlocks.FirstOrDefault(static candidate => candidate.Kind == AsmDataBlockKind.Constant);

        foreach (CogResourceLayout imageLayout in cogResourceLayouts.Images)
        {
            WriteImageCodeBlock(
                sb,
                imageLayout,
                functionsByImage.GetValueOrDefault(imageLayout.Image) ?? [],
                functionIdentifiers,
                includeDefaultBladeHalt,
                cogResourceLayouts);
            WriteImageCogStorageBlocks(sb, imageLayout, registerBlock, constantBlock, functionIdentifiers, cogResourceLayouts);
        }

        WriteSharedStorageBlocks(sb, module.DataBlocks, functionIdentifiers, cogResourceLayouts);
        return sb.ToString();
    }

    private static IReadOnlyDictionary<ImageDescriptor, string> BuildImageIdentifiers(IReadOnlyList<CogResourceLayout> images)
    {
        Dictionary<ImageDescriptor, string> identifiers = [];
        for (int index = 0; index < images.Count; index++)
        {
            ImageDescriptor image = images[index].Image;
            identifiers.Add(image, $"img{index.ToString("D3", CultureInfo.InvariantCulture)}_{BackendSymbolNaming.SanitizeIdentifier(image.Task.Name)}");
        }

        return identifiers;
    }

    private static IReadOnlyDictionary<AsmFunctionKey, string> BuildFunctionIdentifiers(
        IReadOnlyList<AsmFunction> functions,
        IReadOnlyDictionary<ImageDescriptor, string> imageIdentifiers)
    {
        Dictionary<AsmFunction, string> unscopedBaseNames = [];
        Dictionary<string, int> unscopedBaseNameCounts = new(StringComparer.Ordinal);
        foreach (AsmFunction function in functions)
        {
            string baseName = GetUnscopedFunctionIdentifier(function.Symbol);
            unscopedBaseNames.Add(function, baseName);

            if (unscopedBaseNameCounts.TryGetValue(baseName, out int count))
                unscopedBaseNameCounts[baseName] = count + 1;
            else
                unscopedBaseNameCounts.Add(baseName, 1);
        }

        Dictionary<AsmFunctionKey, string> identifiers = [];
        Dictionary<string, int> emittedNameCounts = new(StringComparer.Ordinal);
        Dictionary<ImageDescriptor, string> fallbackImageIdentifiers = [];
        int nextFallbackIndex = imageIdentifiers.Count;
        foreach (AsmFunction function in functions)
        {
            string baseName = unscopedBaseNames[function];
            if (unscopedBaseNameCounts[baseName] > 1)
            {
                if (!imageIdentifiers.TryGetValue(function.OwningImage, out string? imagePrefix))
                {
                    if (!fallbackImageIdentifiers.TryGetValue(function.OwningImage, out imagePrefix))
                    {
                        imagePrefix = $"img{nextFallbackIndex.ToString("D3", CultureInfo.InvariantCulture)}_{BackendSymbolNaming.SanitizeIdentifier(function.OwningImage.Task.Name)}";
                        fallbackImageIdentifiers.Add(function.OwningImage, imagePrefix);
                        nextFallbackIndex++;
                    }
                }

                baseName = $"{imagePrefix}_{baseName}";
            }

            identifiers.Add(function.Key, AllocateUniqueName(emittedNameCounts, baseName));
        }

        return identifiers;
    }

    private static void WriteImageCodeBlock(
        StringBuilder sb,
        CogResourceLayout imageLayout,
        IReadOnlyList<AsmFunction> functions,
        IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers,
        bool includeDefaultBladeHalt,
        CogResourceLayoutSet cogResourceLayouts)
    {
        sb.AppendLine();
        sb.Append("    ' --- image ");
        sb.Append(imageLayout.Image.Task.Name);
        sb.Append(" (");
        sb.Append(imageLayout.Image.ExecutionMode);
        sb.AppendLine(") ---");
        WriteImageCodeOriginDirective(sb, imageLayout);

        foreach (AsmFunction function in functions)
        {
            sb.AppendLine();
            sb.Append("    ' function ");
            sb.Append(function.Name);
            sb.Append(" (");
            sb.Append(function.CcTier);
            sb.AppendLine(")");
            if (function.IsEntryPoint && imageLayout.Image.IsEntryImage)
                sb.AppendLine("  blade_entry");
            sb.Append("  ");
            sb.AppendLine(FormatFunctionIdentifier(functionIdentifiers, function.Key));
            WriteFunctionNodes(sb, function.Nodes, functionIdentifiers, cogResourceLayouts);
            if (includeDefaultBladeHalt && function.IsEntryPoint && imageLayout.Image.IsEntryImage)
                WriteDefaultBladeHalt(sb);
        }
    }

    private static void WriteFunctionNodes(
        StringBuilder sb,
        IReadOnlyList<AsmNode> nodes,
        IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers,
        CogResourceLayoutSet cogResourceLayouts)
    {
        foreach (AsmNode node in nodes)
            WriteNode(sb, node, functionIdentifiers, cogResourceLayouts);
    }

    private static void WriteDefaultBladeHalt(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("    ' halt: default runtime hook");
        sb.AppendLine("  blade_halt");
        sb.AppendLine("    REP #1, #0");
        sb.AppendLine("    NOP");
    }

    private static void WriteSharedStorageBlocks(
        StringBuilder sb,
        IReadOnlyList<AsmDataBlock> dataBlocks,
        IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers,
        CogResourceLayoutSet cogResourceLayouts)
    {
        AsmDataBlock? lutBlock = dataBlocks.FirstOrDefault(static candidate => candidate.Kind == AsmDataBlockKind.Lut);
        if (lutBlock is not null)
        {
            sb.AppendLine();
            sb.AppendLine("    fit $200");
            if (lutBlock.Definitions.OfType<AsmAllocatedStorageDefinition>().Any())
                WriteStorageBlock(sb, lutBlock, "' --- lut file ---", functionIdentifiers, AddressSpace.Lut, cogResourceLayouts);
        }

        AsmDataBlock? hubBlock = dataBlocks.FirstOrDefault(static candidate => candidate.Kind == AsmDataBlockKind.Hub);
        if (hubBlock is not null && hubBlock.Definitions.OfType<AsmAllocatedStorageDefinition>().Any())
        {
            sb.AppendLine();
            WriteStorageBlock(sb, hubBlock, "' --- hub file ---", functionIdentifiers, AddressSpace.Hub, cogResourceLayouts);
        }
    }

    private static void WriteImageCogStorageBlocks(
        StringBuilder sb,
        CogResourceLayout imageLayout,
        AsmDataBlock? registerBlock,
        AsmDataBlock? constantBlock,
        IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers,
        CogResourceLayoutSet cogResourceLayouts)
    {
        List<AsmAllocatedStorageDefinition> definitions = [];
        if (registerBlock is not null)
            definitions.AddRange(registerBlock.Definitions.OfType<AsmAllocatedStorageDefinition>().Where(definition => SymbolBelongsToImage(definition.Symbol, imageLayout.Image)));
        if (constantBlock is not null)
            definitions.AddRange(constantBlock.Definitions.OfType<AsmAllocatedStorageDefinition>().Where(definition => SymbolBelongsToImage(definition.Symbol, imageLayout.Image)));

        sb.AppendLine();
        sb.AppendLine("    ' --- cog data file ---");

        if (definitions.Count == 0)
        {
            if (imageLayout.Image.ExecutionMode is AddressSpace.Lut or AddressSpace.Hub)
                sb.AppendLine("    org $0");
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
                WriteCogOriginDirective(sb, ResolveCogPhysicalAddressBytes(definition.Symbol, cogResourceLayouts), address);

            WriteAllocatedDefinition(sb, definition, functionIdentifiers, maxLabelWidth, maxDirectiveWidth);
            previousEndAddress = address + GetDefinitionSizeInAddressUnits(definition);
        }

        sb.AppendLine("    fit $1F0");
    }

    private static void WriteStorageBlock(
        StringBuilder sb,
        AsmDataBlock block,
        string header,
        IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers,
        AddressSpace storageClass,
        CogResourceLayoutSet cogResourceLayouts)
    {
        if (storageClass == AddressSpace.Hub)
        {
            WriteHubStorageBlock(sb, block, header, functionIdentifiers, cogResourceLayouts);
            return;
        }

        List<AsmAllocatedStorageDefinition> placedDefinitions = block.Definitions
            .OfType<AsmAllocatedStorageDefinition>()
            .Where(definition => definition.Symbol is StoragePlace { ResolvedLayoutSlot: not null })
            .OrderBy(static definition => GetRawAddress(((StoragePlace)definition.Symbol).ResolvedLayoutSlot!.Address))
            .ThenBy(definition => definition.Symbol.Name, StringComparer.Ordinal)
            .ToList();
        if (placedDefinitions.Count == 0)
        {
            if (storageClass == AddressSpace.Lut)
                sb.AppendLine("    org $200");
            else
                sb.AppendLine("    orgh");

            WriteAllocatedBlock(sb, block, header, functionIdentifiers, emitAlignmentDirectives: storageClass == AddressSpace.Hub);
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
            WriteOriginDirective(sb, storageClass, GetRawAddress(slot.Address));
            WriteAllocatedDefinition(sb, definition, functionIdentifiers, maxLabelWidth, maxDirectiveWidth);
            nextAddress = Math.Max(nextAddress, GetRawAddress(slot.Address) + GetDefinitionSizeInAddressUnits(definition));
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
            emitAlignmentDirectives: storageClass == AddressSpace.Hub);
    }

    private static void WriteHubStorageBlock(
        StringBuilder sb,
        AsmDataBlock block,
        string header,
        IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers,
        CogResourceLayoutSet cogResourceLayouts)
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

        sb.AppendLine();
        sb.AppendLine(header);

        if (placedDefinitions.Count == 0 && sequentialDefinitions.Count == 0)
            return;

        sb.AppendLine("    orgh");

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

        int currentAddress = cogResourceLayouts.Images.Count == 0
            ? 0
            : (int)cogResourceLayouts.Images[^1].Placement.HubEndAddressExclusive;
        foreach (AsmAllocatedStorageDefinition definition in placedDefinitions)
        {
            StoragePlace place = (StoragePlace)definition.Symbol;
            LayoutSlot slot = Assert.NotNull(place.ResolvedLayoutSlot);
            int slotAddress = GetRawAddress(slot.Address);
            int paddingBytes = slotAddress - currentAddress;
            Assert.Invariant(paddingBytes >= 0, $"Hub storage address for '{place.Symbol.Name}' moved backwards.");
            if (paddingBytes > 0)
            {
                sb.Append("    BYTE 0[");
                sb.Append(paddingBytes.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("]");
            }

            WriteAllocatedDefinition(sb, definition, functionIdentifiers, maxLabelWidth, maxDirectiveWidth);
            currentAddress = slotAddress + GetDefinitionSizeInAddressUnits(definition);
        }

        if (sequentialDefinitions.Count == 0)
            return;

        AsmDataBlock sequentialBlock = new(block.Kind, sequentialDefinitions);
        WriteAllocatedBlockContents(
            sb,
            sequentialBlock,
            functionIdentifiers,
            maxLabelWidth,
            maxDirectiveWidth,
            emitAlignmentDirectives: true);
    }

    private static void WriteAllocatedBlock(
        StringBuilder sb,
        AsmDataBlock block,
        string header,
        IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers,
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
        IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers,
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
        IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers,
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

    private static void WriteOriginDirective(StringBuilder sb, AddressSpace storageClass, int address)
    {
        Requires.NotNull(sb);
        Requires.NonNegative(address);

        switch (storageClass)
        {
            case AddressSpace.Lut:
                int lutOrigin = checked(0x200 + address);
                sb.Append("    org $");
                sb.AppendLine(lutOrigin.ToString("X", CultureInfo.InvariantCulture));
                break;

            case AddressSpace.Hub:
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

    private static void WriteImageCodeOriginDirective(StringBuilder sb, CogResourceLayout imageLayout)
    {
        if (imageLayout.Image.IsEntryImage)
        {
            sb.AppendLine("    orgh");
            sb.Append("  ");
            sb.AppendLine(BladeImageBaseLabel);
        }
        else
        {
            sb.Append("    orgh ");
            sb.AppendLine(FormatPhysicalAddressExpression(imageLayout.HubStartAddressBytes));
        }

        switch (imageLayout.Image.ExecutionMode)
        {
            case AddressSpace.Cog:
                sb.AppendLine("    org $0");
                break;
            case AddressSpace.Lut:
                sb.AppendLine("    org $200");
                break;
            case AddressSpace.Hub:
                break;
            default:
                Assert.Unreachable($"Unexpected execution mode '{imageLayout.Image.ExecutionMode}'."); // pragma: force-coverage
                break; // pragma: force-coverage
        }
    }

    private static void WriteCogOriginDirective(StringBuilder sb, int physicalAddressBytes, int virtualAddress)
    {
        Requires.NotNull(sb);
        Requires.NonNegative(physicalAddressBytes);
        Requires.InRange(virtualAddress, 0, 0x1FF);

        sb.Append("    orgh ");
        sb.AppendLine(FormatPhysicalAddressExpression(new HubAddress(physicalAddressBytes)));
        sb.Append("    org $");
        sb.AppendLine(virtualAddress.ToString("X", CultureInfo.InvariantCulture));
    }

    private static int ResolveCogAddress(IAsmSymbol symbol, CogResourceLayoutSet cogResourceLayouts)
    {
        Requires.NotNull(symbol);
        Requires.NotNull(cogResourceLayouts);

        return symbol switch
        {
            StoragePlace { ResolvedLayoutSlot: LayoutSlot { StorageClass: AddressSpace.Cog } slot } => GetRawAddress(slot.Address),
            StoragePlace place when cogResourceLayouts.TryGetAddress(place, out MemoryAddress stableAddress) => GetRawAddress(stableAddress.Virtual),
            AsmSpillSlotSymbol spillSlot => (int)spillSlot.Slot,
            AsmSharedConstantSymbol constant when cogResourceLayouts.TryGetAddress(constant, out MemoryAddress constantAddress) => GetRawAddress(constantAddress.Virtual),
            _ => Assert.UnreachableValue<int>($"Missing COG address for symbol '{symbol.Name}'."), // pragma: force-coverage
        };
    }

    private static int ResolveCogPhysicalAddressBytes(IAsmSymbol symbol, CogResourceLayoutSet cogResourceLayouts)
    {
        if (cogResourceLayouts.TryGetAddress(symbol, out MemoryAddress address))
            return (int)address.Physical;

        if (TryGetOwningImage(symbol, out ImageDescriptor? image)
            && cogResourceLayouts.TryGetImageStartAddress(Assert.NotNull(image), out HubAddress owningImageStart)
            && cogResourceLayouts.TryGetAddress(symbol, out MemoryAddress virtualAddress))
        {
            return checked((int)owningImageStart + (GetRawAddress(virtualAddress.Virtual) * 4));
        }

        if (cogResourceLayouts.Images.Count == 1
            && cogResourceLayouts.TryGetAddress(symbol, out MemoryAddress singleImageVirtualAddress))
        {
            return checked((int)cogResourceLayouts.EntryImage.HubStartAddressBytes + (GetRawAddress(singleImageVirtualAddress.Virtual) * 4));
        }

        if (symbol is AsmSpillSlotSymbol spillSlot
            && cogResourceLayouts.TryGetImageStartAddress(spillSlot.Image, out HubAddress spillImageStart))
        {
            return checked((int)spillImageStart + ((int)spillSlot.Slot * 4));
        }

        return Assert.UnreachableValue<int>($"Missing physical hub address for symbol '{symbol.Name}'."); // pragma: force-coverage
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

    private static string FormatDataValue(AsmAllocatedStorageDefinition definition, IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers)
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

    private static string FormatDataOperand(AsmOperand operand, bool useHexFormat, IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers)
    {
        return operand switch
        {
            AsmImmediateOperand { Value: >= 0 } immediate when useHexFormat => $"${immediate.Value:X8}",
            AsmImmediateOperand immediate => immediate.Value.ToString(CultureInfo.InvariantCulture),
            AsmSymbolOperand { Symbol: StoragePlace { StorageClass: AddressSpace.Lut } lutPlace, AddressingMode: AsmSymbolAddressingMode.Immediate } =>
                GetLutVirtualAddressConstantName(lutPlace),
            AsmSymbolOperand symbol => FormatSymbol(symbol.Symbol, functionIdentifiers),
            _ => operand.Format(),
        };
    }

    private static void WriteNode(
        StringBuilder sb,
        AsmNode node,
        IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers,
        CogResourceLayoutSet cogResourceLayouts)
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
                        sb.Append(FormatOperand(instruction, i, functionIdentifiers, cogResourceLayouts));
                    }
                }

                if (instruction.FlagEffect != P2FlagEffect.None)
                {
                    sb.Append(' ');
                    sb.Append(instruction.FlagEffect);
                }

                sb.AppendLine();
                break;

            case AsmInlineDataNode inlineData:
                sb.Append("    ");
                sb.Append(FormatDataDirective(inlineData.Directive));
                if (inlineData.Values.Count > 0)
                {
                    sb.Append(' ');
                    for (int i = 0; i < inlineData.Values.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(", ");
                        sb.Append(FormatInlineDataValue(inlineData.Values[i], functionIdentifiers, cogResourceLayouts));
                    }
                }

                sb.AppendLine();
                break;
        }
    }

    private static string FormatInlineDataValue(
        AsmInlineDataValue value,
        IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers,
        CogResourceLayoutSet cogResourceLayouts)
    {
        return value switch
        {
            AsmInlineDataOperandValue operandValue when operandValue.PreserveImmediateSyntax
                => FormatInlineDataImmediateOperand(operandValue.Operand, functionIdentifiers, cogResourceLayouts),
            AsmInlineDataOperandValue operandValue
                => FormatInlineDataDirectOperand(operandValue.Operand, functionIdentifiers, cogResourceLayouts),
            AsmInlineDataRawSymbolValue raw when raw.PreserveImmediateSyntax
                => "#" + raw.Name,
            AsmInlineDataRawSymbolValue raw
                => raw.Name,
            _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
        };
    }

    private static string FormatInlineDataImmediateOperand(
        AsmOperand operand,
        IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers,
        CogResourceLayoutSet cogResourceLayouts)
    {
        return operand switch
        {
            AsmImmediateOperand immediate => "#" + immediate.Value.ToString(CultureInfo.InvariantCulture),
            AsmSymbolOperand symbol => "#" + FormatSymbol(symbol.Symbol, functionIdentifiers),
            _ => FormatOperandOperandOnly(operand, functionIdentifiers, cogResourceLayouts),
        };
    }

    private static string FormatInlineDataDirectOperand(
        AsmOperand operand,
        IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers,
        CogResourceLayoutSet cogResourceLayouts)
    {
        return operand switch
        {
            AsmImmediateOperand immediate => immediate.Value.ToString(CultureInfo.InvariantCulture),
            AsmSymbolOperand symbol => FormatSymbol(symbol.Symbol, functionIdentifiers),
            _ => FormatOperandOperandOnly(operand, functionIdentifiers, cogResourceLayouts),
        };
    }

    private static string FormatOperand(
        AsmInstructionNode instruction,
        int operandIndex,
        IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers,
        CogResourceLayoutSet cogResourceLayouts)
    {
        AsmOperand operand = instruction.Operands[operandIndex];
        return operand switch
        {
            AsmPhysicalRegisterOperand physical => physical.Name,
            AsmRegisterOperand register => register.Format(),
            AsmImmediateOperand immediate => immediate.Format(),
            AsmAltPlaceholderOperand { Kind: AltPlaceholderKind.Immediate } => "#0",
            AsmAltPlaceholderOperand { Kind: AltPlaceholderKind.Register } => "0",
            AsmSymbolOperand symbol => FormatSymbolOperand(symbol, functionIdentifiers, cogResourceLayouts),
            AsmLabelRefOperand labelRef => labelRef.Format(),
            _ => operand.Format(),
        };
    }

    private static string FormatOperandOperandOnly(
        AsmOperand operand,
        IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers,
        CogResourceLayoutSet cogResourceLayouts)
    {
        return operand switch
        {
            AsmPhysicalRegisterOperand physical => physical.Name,
            AsmRegisterOperand register => register.Format(),
            AsmImmediateOperand immediate => immediate.Format(),
            AsmAltPlaceholderOperand { Kind: AltPlaceholderKind.Immediate } => "#0",
            AsmAltPlaceholderOperand { Kind: AltPlaceholderKind.Register } => "0",
            AsmSymbolOperand symbol => FormatSymbolOperand(symbol, functionIdentifiers, cogResourceLayouts),
            AsmLabelRefOperand labelRef => labelRef.Format(),
            _ => operand.Format(),
        };
    }

    private static string FormatSymbolOperand(
        AsmSymbolOperand operand,
        IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers,
        CogResourceLayoutSet cogResourceLayouts)
    {
        if (operand.Symbol is StoragePlace { StorageClass: AddressSpace.Lut } lutPlace
            && operand.AddressingMode == AsmSymbolAddressingMode.Immediate)
        {
            return FormatImmediateOffsetExpression(GetLutVirtualAddressConstantName(lutPlace), operand.Offset, useLongImmediate: true);
        }

        if (operand.Symbol is AsmImageStartSymbol imageStart
            && operand.AddressingMode == AsmSymbolAddressingMode.Immediate)
        {
            bool found = cogResourceLayouts.TryGetImageStartAddress(imageStart.Image, out HubAddress addressBytes);
            Assert.Invariant(found, $"Missing image start address for task '{imageStart.Image.Task.Name}'.");
            return FormatImmediateOffsetExpression(FormatPhysicalAddressExpression(addressBytes), operand.Offset, useLongImmediate: true);
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

    private static string FormatSymbol(IAsmSymbol symbol, IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers)
    {
        return symbol switch
        {
            AsmSpecialRegisterSymbol => symbol.Name,
            AsmCurrentAddressSymbol => symbol.Name,
            AsmFunction function => FormatFunctionIdentifier(functionIdentifiers, function.Key),
            AsmFunctionReferenceSymbol functionReference => FormatFunctionIdentifier(functionIdentifiers, new AsmFunctionKey(functionReference.Image, functionReference.Function)),
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

    private static string FormatFunctionIdentifier(IReadOnlyDictionary<AsmFunctionKey, string> functionIdentifiers, AsmFunctionKey function)
    {
        return functionIdentifiers[function];
    }

    private static string GetUnscopedFunctionIdentifier(FunctionSymbol function)
    {
        Requires.NotNull(function);
        return $"f_{BackendSymbolNaming.SanitizeIdentifier(function.Name)}";
    }

    private static string AllocateUniqueName(IDictionary<string, int> emittedNameCounts, string baseName)
    {
        Requires.NotNull(emittedNameCounts);
        Requires.NotNullOrWhiteSpace(baseName);

        if (emittedNameCounts.TryGetValue(baseName, out int seenCount))
        {
            int nextCount = seenCount + 1;
            emittedNameCounts[baseName] = nextCount;
            return $"{baseName}_{nextCount}";
        }

        emittedNameCounts.Add(baseName, 1);
        return baseName;
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

    private static string GetLutVirtualAddressConstantName(StoragePlace place)
    {
        return $"{FormatSymbol(place)}_vaddr";
    }

    private static string FormatPhysicalAddressExpression(HubAddress addressBytes)
    {
        int rawAddressBytes = (int)addressBytes;
        Requires.NonNegative(rawAddressBytes);
        if (rawAddressBytes == 0)
            return BladeImageBaseLabel;

        return $"{BladeImageBaseLabel} + ${rawAddressBytes:X}";
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
