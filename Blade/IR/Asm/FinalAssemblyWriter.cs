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
    private const string BladeEntryLabel = "blade_entry";
    private const string DefaultHaltLabel = "blade_halt";

    private sealed class LabelNameEmitter
    {
        private readonly record struct ScopedControlFlowLabelKey(AsmFunctionKey Function, ControlFlowLabelSymbol Label);

        private readonly Dictionary<object, string> _emittedNames = [];
        private readonly Dictionary<string, int> _usedNames = new(StringComparer.Ordinal);

        public string GetLabelName(IAsmSymbol symbol, AsmFunction? currentFunction = null)
        {
            Requires.NotNull(symbol);

            if (symbol is AsmSpecialRegisterSymbol or AsmCurrentAddressSymbol)
                return symbol.Name;

            object key = GetSymbolKey(symbol, currentFunction);
            if (_emittedNames.TryGetValue(key, out string? existingName))
                return existingName;

            string emittedName = AllocateUniqueName(GetBaseName(symbol));
            _emittedNames.Add(key, emittedName);
            return emittedName;
        }

        public string GetReservedLabelName(string name)
        {
            Requires.NotNullOrWhiteSpace(name);
            object key = name == DefaultHaltLabel
                ? $"label:{DefaultHaltLabel}"
                : $"reserved:{name}";
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

            if (_emittedNames.TryGetValue(key, out string? existingName))
                return existingName;

            string emittedName = AllocateUniqueName(baseName);
            _emittedNames.Add(key, emittedName);
            return emittedName;
        }

        private string AllocateUniqueName(string baseName)
        {
            Requires.NotNullOrWhiteSpace(baseName);

            if (_usedNames.TryGetValue(baseName, out int seenCount))
            {
                int nextCount = seenCount + 1;
                _usedNames[baseName] = nextCount;
                return $"{baseName}_{nextCount}";
            }

            _usedNames.Add(baseName, 1);
            return baseName;
        }

        private static object GetSymbolKey(IAsmSymbol symbol, AsmFunction? currentFunction)
        {
            return symbol switch
            {
                AsmFunction function => function.Key,
                AsmFunctionReferenceSymbol functionReference => new AsmFunctionKey(functionReference.Image, functionReference.Function),
                ControlFlowLabelSymbol label when currentFunction is not null && label.Name != DefaultHaltLabel => new ScopedControlFlowLabelKey(currentFunction.Key, label),
                ControlFlowLabelSymbol { Name: DefaultHaltLabel } => $"label:{DefaultHaltLabel}",
                _ => symbol,
            };
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

    public static string Write(IReadOnlyList<AsmModule> modules, CogResourceLayoutSet cogResourceLayouts)
    {
        return Build(modules, cogResourceLayouts).Text;
    }

    public static FinalAssembly Build(IReadOnlyList<AsmModule> modules, CogResourceLayoutSet cogResourceLayouts)
    {
        Requires.NotNull(modules);
        Requires.NotNull(cogResourceLayouts);

        LabelNameEmitter labelNames = new();
        string conSectionContents = WriteConSectionContents(modules, cogResourceLayouts, labelNames);
        string datSectionContents = WriteDatSectionContents(modules, cogResourceLayouts, labelNames, includeDefaultBladeHalt: true);
        return FinalAssemblyComposer.Compose(conSectionContents, datSectionContents);
    }

    public static string WriteConSectionContents(IReadOnlyList<AsmModule> modules, CogResourceLayoutSet cogResourceLayouts)
    {
        return WriteConSectionContents(modules, cogResourceLayouts, new LabelNameEmitter());
    }

    private static string WriteConSectionContents(IReadOnlyList<AsmModule> modules, CogResourceLayoutSet cogResourceLayouts, LabelNameEmitter labelNames)
    {
        Requires.NotNull(modules);
        Requires.NotNull(cogResourceLayouts);
        Requires.NotNull(labelNames);

        IReadOnlyList<AsmDataBlock> dataBlocks = MergeDataBlocks(modules);
        StringBuilder sb = new();
        AsmDataBlock? externalBlock = dataBlocks.FirstOrDefault(static block => block.Kind == AsmDataBlockKind.External);
        if (externalBlock is not null)
        {
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
                sb.Append(GetLabelName(place, labelNames));
                sb.Append(" = $");
                sb.Append(GetRawAddress(virtualAddress.Value).ToString("X", CultureInfo.InvariantCulture));
                sb.AppendLine();
            }
        }

        AsmDataBlock? lutBlock = dataBlocks.FirstOrDefault(static block => block.Kind == AsmDataBlockKind.Lut);
        if (lutBlock is not null)
        {
            foreach (AsmAllocatedStorageDefinition definition in lutBlock.Definitions.OfType<AsmAllocatedStorageDefinition>())
            {
                if (definition.Symbol is not StoragePlace place)
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
                sb.Append(GetLutVirtualAddressConstantName(place, labelNames));
                sb.Append(" = $");
                sb.Append(GetRawAddress(virtualAddress.Value).ToString("X", CultureInfo.InvariantCulture));
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    public static string WriteDatSectionContents(IReadOnlyList<AsmModule> modules, CogResourceLayoutSet cogResourceLayouts)
    {
        return WriteDatSectionContents(modules, cogResourceLayouts, new LabelNameEmitter(), includeDefaultBladeHalt: false);
    }

    private static string WriteDatSectionContents(
        IReadOnlyList<AsmModule> modules,
        CogResourceLayoutSet cogResourceLayouts,
        LabelNameEmitter labelNames,
        bool includeDefaultBladeHalt)
    {
        Requires.NotNull(modules);
        Requires.NotNull(cogResourceLayouts);
        Requires.NotNull(labelNames);

        IReadOnlyList<AsmFunction> functions = modules.SelectMany(static module => module.Functions).ToList();
        IReadOnlyList<AsmDataBlock> dataBlocks = MergeDataBlocks(modules);
        StringBuilder sb = new();
        sb.AppendLine("    ' --- Blade compiler output ---");

        Dictionary<ImageDescriptor, IReadOnlyList<AsmFunction>> functionsByImage = functions
            .GroupBy(static function => function.OwningImage)
            .ToDictionary(static group => group.Key, static group => (IReadOnlyList<AsmFunction>)group.ToList());
        AsmDataBlock? registerBlock = dataBlocks.FirstOrDefault(static candidate => candidate.Kind == AsmDataBlockKind.Register);
        AsmDataBlock? constantBlock = dataBlocks.FirstOrDefault(static candidate => candidate.Kind == AsmDataBlockKind.Constant);

        foreach (CogResourceLayout imageLayout in cogResourceLayouts.Images)
        {
            WriteImageCodeBlock(
                sb,
                imageLayout,
                functionsByImage.GetValueOrDefault(imageLayout.Image) ?? [],
                labelNames,
                includeDefaultBladeHalt,
                cogResourceLayouts);
            WriteImageCogStorageBlocks(sb, imageLayout, registerBlock, constantBlock, labelNames, cogResourceLayouts);
        }

        WriteSharedStorageBlocks(sb, dataBlocks, labelNames, cogResourceLayouts);
        return sb.ToString();
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

    private static void WriteImageCodeBlock(
        StringBuilder sb,
        CogResourceLayout imageLayout,
        IReadOnlyList<AsmFunction> functions,
        LabelNameEmitter labelNames,
        bool includeDefaultBladeHalt,
        CogResourceLayoutSet cogResourceLayouts)
    {
        sb.AppendLine();
        sb.Append("    ' --- image ");
        sb.Append(imageLayout.Image.Task.Name);
        sb.Append(" (");
        sb.Append(imageLayout.Image.ExecutionMode);
        sb.AppendLine(") ---");
        WriteImageCodeOriginDirective(sb, imageLayout, labelNames);

        foreach (AsmFunction function in functions)
        {
            sb.AppendLine();
            sb.Append("    ' function ");
            sb.Append(function.Name);
            sb.Append(" (");
            sb.Append(function.CcTier);
            sb.AppendLine(")");
            if (function.IsEntryPoint && imageLayout.Image.IsEntryImage)
            {
                sb.Append("  ");
                sb.AppendLine(labelNames.GetReservedLabelName(BladeEntryLabel));
            }
            sb.Append("  ");
            sb.AppendLine(GetLabelName(function, labelNames));
            WriteFunctionNodes(sb, function, function.Nodes, labelNames, cogResourceLayouts);
            if (includeDefaultBladeHalt && function.IsEntryPoint && imageLayout.Image.IsEntryImage)
                WriteDefaultBladeHalt(sb, labelNames);
        }
    }

    private static void WriteFunctionNodes(
        StringBuilder sb,
        AsmFunction function,
        IReadOnlyList<AsmNode> nodes,
        LabelNameEmitter labelNames,
        CogResourceLayoutSet cogResourceLayouts)
    {
        foreach (AsmNode node in nodes)
            WriteNode(sb, node, function, labelNames, cogResourceLayouts);
    }

    private static void WriteDefaultBladeHalt(StringBuilder sb, LabelNameEmitter labelNames)
    {
        sb.AppendLine();
        sb.AppendLine("    ' halt: default runtime hook");
        sb.Append("  ");
        sb.AppendLine(labelNames.GetReservedLabelName(DefaultHaltLabel));
        sb.AppendLine("    REP #1, #0");
        sb.AppendLine("    NOP");
    }

    private static void WriteSharedStorageBlocks(
        StringBuilder sb,
        IReadOnlyList<AsmDataBlock> dataBlocks,
        LabelNameEmitter labelNames,
        CogResourceLayoutSet cogResourceLayouts)
    {
        AsmDataBlock? hubBlock = dataBlocks.FirstOrDefault(static candidate => candidate.Kind == AsmDataBlockKind.Hub);
        if (hubBlock is not null && hubBlock.Definitions.OfType<AsmAllocatedStorageDefinition>().Any())
        {
            sb.AppendLine();
            WriteStorageBlock(sb, hubBlock, "' --- hub file ---", labelNames, AddressSpace.Hub, cogResourceLayouts);
        }
    }

    private static void WriteImageCogStorageBlocks(
        StringBuilder sb,
        CogResourceLayout imageLayout,
        AsmDataBlock? registerBlock,
        AsmDataBlock? constantBlock,
        LabelNameEmitter labelNames,
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
            .ThenBy(definition => GetLabelName(definition.Symbol, labelNames), StringComparer.Ordinal)
            .ToList();

        int maxLabelWidth = definitions.Max(definition => GetLabelName(definition.Symbol, labelNames).Length);
        int maxDirectiveWidth = definitions.Max(static definition => FormatDataDirective(definition.Directive).Length);

        int? previousEndAddress = null;
        foreach (AsmAllocatedStorageDefinition definition in definitions)
        {
            int address = ResolveCogAddress(definition.Symbol, cogResourceLayouts);
            if (previousEndAddress != address)
                WriteCogOriginDirective(sb, ResolveCogPhysicalAddressBytes(definition.Symbol, cogResourceLayouts), address, labelNames);

            WriteAllocatedDefinition(sb, definition, labelNames, maxLabelWidth, maxDirectiveWidth);
            previousEndAddress = address + GetDefinitionSizeInAddressUnits(definition);
        }

        sb.AppendLine("    fit $1F0");
    }

    private static void WriteStorageBlock(
        StringBuilder sb,
        AsmDataBlock block,
        string header,
        LabelNameEmitter labelNames,
        AddressSpace storageClass,
        CogResourceLayoutSet cogResourceLayouts)
    {
        if (storageClass == AddressSpace.Hub)
        {
            WriteHubStorageBlock(sb, block, header, labelNames, cogResourceLayouts);
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

            WriteAllocatedBlock(sb, block, header, labelNames, emitAlignmentDirectives: storageClass == AddressSpace.Hub);
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
            .Select(definition => GetLabelName(definition.Symbol, labelNames).Length)
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
            WriteAllocatedDefinition(sb, definition, labelNames, maxLabelWidth, maxDirectiveWidth);
            nextAddress = Math.Max(nextAddress, GetRawAddress(slot.Address) + GetDefinitionSizeInAddressUnits(definition));
        }

        if (sequentialDefinitions.Count == 0)
            return;

        WriteOriginDirective(sb, storageClass, nextAddress);
        AsmDataBlock sequentialBlock = new(block.Kind, sequentialDefinitions);
        WriteAllocatedBlockContents(
            sb,
            sequentialBlock,
            labelNames,
            maxLabelWidth,
            maxDirectiveWidth,
            emitAlignmentDirectives: storageClass == AddressSpace.Hub);
    }

    private static void WriteHubStorageBlock(
        StringBuilder sb,
        AsmDataBlock block,
        string header,
        LabelNameEmitter labelNames,
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
            .Select(definition => GetLabelName(definition.Symbol, labelNames).Length)
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

            WriteAllocatedDefinition(sb, definition, labelNames, maxLabelWidth, maxDirectiveWidth);
            currentAddress = slotAddress + GetDefinitionSizeInAddressUnits(definition);
        }

        if (sequentialDefinitions.Count == 0)
            return;

        AsmDataBlock sequentialBlock = new(block.Kind, sequentialDefinitions);
        WriteAllocatedBlockContents(
            sb,
            sequentialBlock,
            labelNames,
            maxLabelWidth,
            maxDirectiveWidth,
            emitAlignmentDirectives: true);
    }

    private static void WriteAllocatedBlock(
        StringBuilder sb,
        AsmDataBlock block,
        string header,
        LabelNameEmitter labelNames,
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

        int maxLabelWidth = definitions.Max(definition => GetLabelName(definition.Symbol, labelNames).Length);
        int maxDirectiveWidth = definitions.Max(static definition => FormatDataDirective(definition.Directive).Length);
        WriteAllocatedBlockContents(sb, block, labelNames, maxLabelWidth, maxDirectiveWidth, emitAlignmentDirectives);
    }

    private static void WriteAllocatedBlockContents(
        StringBuilder sb,
        AsmDataBlock block,
        LabelNameEmitter labelNames,
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

            WriteAllocatedDefinition(sb, definition, labelNames, maxLabelWidth, maxDirectiveWidth);
        }
    }

    private static void WriteAllocatedDefinition(
        StringBuilder sb,
        AsmAllocatedStorageDefinition definition,
        LabelNameEmitter labelNames,
        int maxLabelWidth,
        int maxDirectiveWidth)
    {
        string label = GetLabelName(definition.Symbol, labelNames);
        string directive = FormatDataDirective(definition.Directive);
        string value = FormatDataValue(definition, labelNames);

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

    private static void WriteImageCodeOriginDirective(StringBuilder sb, CogResourceLayout imageLayout, LabelNameEmitter labelNames)
    {
        if (imageLayout.Image.IsEntryImage)
        {
            sb.AppendLine("    orgh");
            sb.Append("  ");
            sb.AppendLine(labelNames.GetReservedLabelName(BladeImageBaseLabel));
        }
        else
        {
            sb.Append("    orgh ");
            sb.AppendLine(FormatPhysicalAddressExpression(imageLayout.HubStartAddressBytes, labelNames));
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

    private static void WriteCogOriginDirective(StringBuilder sb, int physicalAddressBytes, int virtualAddress, LabelNameEmitter labelNames)
    {
        Requires.NotNull(sb);
        Requires.NonNegative(physicalAddressBytes);
        Requires.InRange(virtualAddress, 0, 0x1FF);

        sb.Append("    orgh ");
        sb.AppendLine(FormatPhysicalAddressExpression(new HubAddress(physicalAddressBytes), labelNames));
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

    private static string FormatDataValue(AsmAllocatedStorageDefinition definition, LabelNameEmitter labelNames)
    {
        if (definition.InitialValues is null || definition.InitialValues.Count == 0)
            return definition.Count > 1 ? $"0[{definition.Count}]" : "0";

        if (definition.InitialValues.Count == 1)
        {
            string initializer = FormatDataOperand(definition.InitialValues[0], definition.UseHexFormat, labelNames);
            return definition.Count > 1 ? $"{initializer}[{definition.Count}]" : initializer;
        }

        List<string> values = new(definition.InitialValues.Count);
        foreach (AsmOperand operand in definition.InitialValues)
            values.Add(FormatDataOperand(operand, definition.UseHexFormat, labelNames));
        return string.Join(", ", values);
    }

    private static string FormatDataOperand(AsmOperand operand, bool useHexFormat, LabelNameEmitter labelNames)
    {
        return operand switch
        {
            AsmImmediateOperand { Value: >= 0 } immediate when useHexFormat => $"${immediate.Value:X8}",
            AsmImmediateOperand immediate => immediate.Value.ToString(CultureInfo.InvariantCulture),
            AsmSymbolOperand symbol => GetLabelName(symbol.Symbol, labelNames),
            _ => operand.Format(),
        };
    }

    private static void WriteNode(
        StringBuilder sb,
        AsmNode node,
        AsmFunction currentFunction,
        LabelNameEmitter labelNames,
        CogResourceLayoutSet cogResourceLayouts)
    {
        switch (node)
        {
            case AsmLabelNode label:
                sb.Append("  ");
                sb.AppendLine(GetLabelName(label.Label, labelNames, currentFunction));
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
                        sb.Append(FormatOperand(instruction, i, labelNames, cogResourceLayouts, currentFunction));
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
                        sb.Append(FormatInlineDataValue(inlineData.Values[i], labelNames, cogResourceLayouts, currentFunction));
                    }
                }

                sb.AppendLine();
                break;
        }
    }

    private static string FormatInlineDataValue(
        AsmInlineDataValue value,
        LabelNameEmitter labelNames,
        CogResourceLayoutSet cogResourceLayouts,
        AsmFunction currentFunction)
    {
        return value switch
        {
            AsmInlineDataOperandValue operandValue when operandValue.PreserveImmediateSyntax
                => FormatInlineDataImmediateOperand(operandValue.Operand, labelNames, cogResourceLayouts, currentFunction),
            AsmInlineDataOperandValue operandValue
                => FormatInlineDataDirectOperand(operandValue.Operand, labelNames, cogResourceLayouts, currentFunction),
            AsmInlineDataRawSymbolValue raw when raw.PreserveImmediateSyntax
                => "#" + raw.Name,
            AsmInlineDataRawSymbolValue raw
                => raw.Name,
            _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
        };
    }

    private static string FormatInlineDataImmediateOperand(
        AsmOperand operand,
        LabelNameEmitter labelNames,
        CogResourceLayoutSet cogResourceLayouts,
        AsmFunction currentFunction)
    {
        return operand switch
        {
            AsmImmediateOperand immediate => "#" + immediate.Value.ToString(CultureInfo.InvariantCulture),
            AsmSymbolOperand symbol => "#" + GetLabelName(symbol.Symbol, labelNames, currentFunction),
            _ => FormatOperandOperandOnly(operand, labelNames, cogResourceLayouts, currentFunction),
        };
    }

    private static string FormatInlineDataDirectOperand(
        AsmOperand operand,
        LabelNameEmitter labelNames,
        CogResourceLayoutSet cogResourceLayouts,
        AsmFunction currentFunction)
    {
        return operand switch
        {
            AsmImmediateOperand immediate => immediate.Value.ToString(CultureInfo.InvariantCulture),
            AsmSymbolOperand symbol => GetLabelName(symbol.Symbol, labelNames, currentFunction),
            _ => FormatOperandOperandOnly(operand, labelNames, cogResourceLayouts, currentFunction),
        };
    }

    private static string FormatOperand(
        AsmInstructionNode instruction,
        int operandIndex,
        LabelNameEmitter labelNames,
        CogResourceLayoutSet cogResourceLayouts,
        AsmFunction currentFunction)
    {
        AsmOperand operand = instruction.Operands[operandIndex];
        return operand switch
        {
            AsmPhysicalRegisterOperand physical => physical.Name,
            AsmRegisterOperand register => register.Format(),
            AsmImmediateOperand immediate => immediate.Format(),
            AsmAltPlaceholderOperand { Kind: AltPlaceholderKind.Immediate } => "#0",
            AsmAltPlaceholderOperand { Kind: AltPlaceholderKind.Register } => "0",
            AsmSymbolOperand symbol => FormatSymbolOperand(instruction, operandIndex, symbol, labelNames, cogResourceLayouts, currentFunction),
            AsmLabelRefOperand labelRef => FormatLabelRefOperand(labelRef, labelNames, currentFunction),
            _ => operand.Format(),
        };
    }

    private static string FormatOperandOperandOnly(
        AsmOperand operand,
        LabelNameEmitter labelNames,
        CogResourceLayoutSet cogResourceLayouts,
        AsmFunction currentFunction)
    {
        return operand switch
        {
            AsmPhysicalRegisterOperand physical => physical.Name,
            AsmRegisterOperand register => register.Format(),
            AsmImmediateOperand immediate => immediate.Format(),
            AsmAltPlaceholderOperand { Kind: AltPlaceholderKind.Immediate } => "#0",
            AsmAltPlaceholderOperand { Kind: AltPlaceholderKind.Register } => "0",
            AsmSymbolOperand symbol => FormatSymbolOperand(symbol, labelNames, cogResourceLayouts, currentFunction),
            AsmLabelRefOperand labelRef => FormatLabelRefOperand(labelRef, labelNames, currentFunction),
            _ => operand.Format(),
        };
    }

    private static string FormatLabelRefOperand(AsmLabelRefOperand operand, LabelNameEmitter labelNames, AsmFunction currentFunction)
    {
        Requires.NotNull(operand);
        Requires.NotNull(labelNames);
        Requires.NotNull(currentFunction);
        return $"@{GetLabelName(operand.Label, labelNames, currentFunction)}";
    }

    private static string FormatSymbolOperand(
        AsmInstructionNode instruction,
        int operandIndex,
        AsmSymbolOperand operand,
        LabelNameEmitter labelNames,
        CogResourceLayoutSet cogResourceLayouts,
        AsmFunction currentFunction)
    {
        if (operand.Symbol is AsmImageStartSymbol imageStart
            && operand.AddressingMode == AsmSymbolAddressingMode.Immediate)
        {
            bool found = cogResourceLayouts.TryGetImageStartAddress(imageStart.Image, out HubAddress addressBytes);
            Assert.Invariant(found, $"Missing image start address for task '{imageStart.Image.Task.Name}'.");
            return FormatImmediateOffsetExpression(FormatPhysicalAddressExpression(addressBytes, labelNames), operand.Offset, useLongImmediate: true);
        }

        bool isImmediateLutSymbol = operand.Symbol.SymbolType == SymbolType.LutVariable
            && operand.AddressingMode == AsmSymbolAddressingMode.Immediate;
        string formatted = isImmediateLutSymbol
            ? GetLutVirtualAddressConstantName(operand.Symbol, labelNames, currentFunction)
            : GetLabelName(operand.Symbol, labelNames, currentFunction);
        if (operand.AddressingMode == AsmSymbolAddressingMode.Immediate)
        {
            bool useLongImmediate = operand.Symbol is StoragePlace;
            if (instruction.Mnemonic is P2Mnemonic.RDLUT or P2Mnemonic.WRLUT
                && operandIndex == 1
                && isImmediateLutSymbol)
            {
                useLongImmediate = false;
            }

            return FormatImmediateOffsetExpression(formatted, operand.Offset, useLongImmediate);
        }

        if (operand.Offset != 0)
            formatted = operand.Offset > 0 ? $"{formatted} + {operand.Offset}" : $"{formatted} - {-operand.Offset}";

        return formatted;
    }

    private static string FormatSymbolOperand(
        AsmSymbolOperand operand,
        LabelNameEmitter labelNames,
        CogResourceLayoutSet cogResourceLayouts,
        AsmFunction currentFunction)
    {
        Requires.NotNull(operand);
        Requires.NotNull(labelNames);
        Requires.NotNull(cogResourceLayouts);
        Requires.NotNull(currentFunction);

        if (operand.Symbol is AsmImageStartSymbol imageStart
            && operand.AddressingMode == AsmSymbolAddressingMode.Immediate)
        {
            bool found = cogResourceLayouts.TryGetImageStartAddress(imageStart.Image, out HubAddress addressBytes);
            Assert.Invariant(found, $"Missing image start address for task '{imageStart.Image.Task.Name}'.");
            return FormatImmediateOffsetExpression(FormatPhysicalAddressExpression(addressBytes, labelNames), operand.Offset, useLongImmediate: true);
        }

        bool isImmediateLutSymbol = operand.Symbol.SymbolType == SymbolType.LutVariable
            && operand.AddressingMode == AsmSymbolAddressingMode.Immediate;
        string formatted = isImmediateLutSymbol
            ? GetLutVirtualAddressConstantName(operand.Symbol, labelNames, currentFunction)
            : GetLabelName(operand.Symbol, labelNames, currentFunction);
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

    private static string GetLabelName(IAsmSymbol symbol, LabelNameEmitter labelNames, AsmFunction? currentFunction = null)
    {
        Requires.NotNull(symbol);
        Requires.NotNull(labelNames);
        return labelNames.GetLabelName(symbol, currentFunction);
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

    private static string GetLutVirtualAddressConstantName(StoragePlace place, LabelNameEmitter labelNames)
    {
        Requires.NotNull(place);
        Requires.NotNull(labelNames);
        return labelNames.GetLutVirtualAddressConstantName(place);
    }

    private static string GetLutVirtualAddressConstantName(IAsmSymbol symbol, LabelNameEmitter labelNames, AsmFunction? currentFunction)
    {
        Requires.NotNull(symbol);
        Requires.NotNull(labelNames);

        if (symbol is StoragePlace place)
            return GetLutVirtualAddressConstantName(place, labelNames);

        return $"{GetLabelName(symbol, labelNames, currentFunction)}_vaddr";
    }

    private static string FormatPhysicalAddressExpression(HubAddress addressBytes, LabelNameEmitter labelNames)
    {
        int rawAddressBytes = (int)addressBytes;
        Requires.NonNegative(rawAddressBytes);
        if (rawAddressBytes == 0)
            return labelNames.GetReservedLabelName(BladeImageBaseLabel);

        return $"{labelNames.GetReservedLabelName(BladeImageBaseLabel)} + ${rawAddressBytes:X}";
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
