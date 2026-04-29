using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Blade.Diagnostics;
using Blade.IR.Asm;
using Blade.Semantics;
using Blade.Source;

namespace Blade.IR;

/// <summary>
/// Describes the register-space layout for one emitted image.
/// </summary>
public sealed class CogResourceLayout(
    ImagePlacementEntry placement,
    int codeSizeLongs,
    IReadOnlyList<CogAddress> availableRegisterAddresses,
    IReadOnlyList<IAsmSymbol> stableSymbols)
{
    private readonly HashSet<CogAddress> _availableRegisterAddresses = [.. availableRegisterAddresses];
    private readonly HashSet<IAsmSymbol> _stableSymbols = [.. stableSymbols];

    /// <summary>
    /// Gets the physical image placement that owns this register-space layout.
    /// </summary>
    public ImagePlacementEntry Placement { get; } = Requires.NotNull(placement);

    /// <summary>
    /// Gets the logical image whose COG resources are being described.
    /// </summary>
    public ImageDescriptor Image => Placement.Image;

    /// <summary>
    /// Gets the number of instruction longs emitted for this image's executable body.
    /// </summary>
    public int CodeSizeLongs { get; } = Requires.NonNegative(codeSizeLongs);

    /// <summary>
    /// Gets the remaining concrete register addresses available for allocation.
    /// </summary>
    public IReadOnlyList<CogAddress> AvailableRegisterAddresses { get; } = Requires.NotNull(availableRegisterAddresses);

    /// <summary>
    /// Gets the stable register-backed symbols physically present in this image.
    /// </summary>
    public IReadOnlyList<IAsmSymbol> StableSymbols { get; } = Requires.NotNull(stableSymbols);

    /// <summary>
    /// Gets the physical hub start address of this image in bytes.
    /// </summary>
    public HubAddress HubStartAddressBytes => Placement.HubStartAddressBytes;

    /// <summary>
    /// Returns whether the supplied concrete register address is free in this image.
    /// </summary>
    public bool IsRegisterAddressAvailable(CogAddress address)
    {
        return _availableRegisterAddresses.Contains(address);
    }

    /// <summary>
    /// Returns whether the supplied register-backed symbol is physically present in this image.
    /// </summary>
    public bool ContainsStableSymbol(IAsmSymbol symbol)
    {
        Requires.NotNull(symbol);
        return _stableSymbols.Contains(symbol);
    }
}

/// <summary>
/// Exposes per-image register-space layouts plus virtual and physical addresses for emitted symbols.
/// </summary>
public sealed class CogResourceLayoutSet
{
    private readonly IReadOnlyDictionary<IAsmSymbol, MemoryAddress> _addressBySymbol;
    private readonly IReadOnlyDictionary<ImageDescriptor, CogResourceLayout> _layoutsByImage;
    private readonly IReadOnlyDictionary<StoragePlace, CogResourceLayout> _layoutsByOwnedPlace;

    public CogResourceLayoutSet(
        IReadOnlyList<CogResourceLayout> images,
        CogResourceLayout entryImage,
        IReadOnlyDictionary<IAsmSymbol, MemoryAddress> addressBySymbol,
        IReadOnlyDictionary<ImageDescriptor, CogResourceLayout> layoutsByImage,
        IReadOnlyDictionary<StoragePlace, CogResourceLayout> layoutsByOwnedPlace,
        int maximumCodeSizeLongs)
    {
        Requires.NotNull(images);
        Requires.NotNull(entryImage);
        Requires.NotNull(addressBySymbol);
        Requires.NotNull(layoutsByImage);
        Requires.NotNull(layoutsByOwnedPlace);
        Requires.That(images.Contains(entryImage));

        Images = images;
        EntryImage = entryImage;
        MaximumCodeSizeLongs = Requires.NonNegative(maximumCodeSizeLongs);
        _addressBySymbol = addressBySymbol;
        _layoutsByImage = layoutsByImage;
        _layoutsByOwnedPlace = layoutsByOwnedPlace;
    }

    /// <summary>
    /// Gets the per-image register-space layouts in hub-placement order.
    /// </summary>
    public IReadOnlyList<CogResourceLayout> Images { get; }

    /// <summary>
    /// Gets the entry image layout.
    /// </summary>
    public CogResourceLayout EntryImage { get; }

    /// <summary>
    /// Gets the largest executable body among all images.
    /// </summary>
    public int MaximumCodeSizeLongs { get; }

    /// <summary>
    /// Tries to get the virtual register-space address for one emitted symbol.
    /// </summary>
    public bool TryGetAddress(IAsmSymbol symbol, out MemoryAddress address)
    {
        Requires.NotNull(symbol);
        return _addressBySymbol.TryGetValue(symbol, out address);
    }

    /// <summary>
    /// Tries to get the image layout for one logical image.
    /// </summary>
    public bool TryGetLayout(ImageDescriptor image, out CogResourceLayout? layout)
    {
        Requires.NotNull(image);
        return _layoutsByImage.TryGetValue(image, out layout);
    }

    /// <summary>
    /// Tries to get the image layout that owns one image-local storage place.
    /// </summary>
    public bool TryGetOwningLayout(StoragePlace place, out CogResourceLayout? layout)
    {
        Requires.NotNull(place);
        return _layoutsByOwnedPlace.TryGetValue(place, out layout);
    }

    /// <summary>
    /// Tries to get the physical hub start address for one image.
    /// </summary>
    public bool TryGetImageStartAddress(ImageDescriptor image, out HubAddress addressBytes)
    {
        Requires.NotNull(image);
        if (_layoutsByImage.TryGetValue(image, out CogResourceLayout? layout))
        {
            addressBytes = layout.HubStartAddressBytes;
            return true;
        }

        addressBytes = default;
        return false;
    }
}

/// <summary>
/// Plans per-image stable register-backed storage and free allocation space.
/// </summary>
public static class CogResourcePlanner
{
    private const int FirstSpecialRegisterAddress = 0x1F0;
    private const int FirstNonSpecialAddress = 0x000;
    private const int LastAllocatableAddress = 0x1EF;
    private const int DefaultBladeHaltInstructionCount = 2;

    private readonly record struct OccupiedRange(int StartAddress, int EndAddressExclusive);

    /// <summary>
    /// Builds the image-local register-space layouts after code-size-changing passes completed.
    /// </summary>
    public static CogResourceLayoutSet Build(
        AsmModule module,
        ImagePlan imagePlan,
        ImagePlacement imagePlacement,
        LayoutSolution layoutSolution,
        bool includeDefaultBladeHalt,
        DiagnosticBag? diagnostics)
    {
        Requires.NotNull(module);
        Requires.NotNull(imagePlan);
        Requires.NotNull(imagePlacement);
        Requires.NotNull(layoutSolution);

        Dictionary<ImageDescriptor, IReadOnlyList<AsmFunction>> functionsByImage = module.Functions
            .GroupBy(static function => function.OwningImage)
            .ToDictionary(static group => group.Key, static group => (IReadOnlyList<AsmFunction>)group.ToList());

        Dictionary<ImageDescriptor, int> codeSizeByImage = [];
        int maximumCodeSizeLongs = 0;
        foreach (ImagePlacementEntry placement in imagePlacement.Images)
        {
            int codeSizeLongs = CountImageCodeLongs(
                placement.Image,
                functionsByImage.GetValueOrDefault(placement.Image) ?? [],
                includeDefaultBladeHalt);
            codeSizeByImage.Add(placement.Image, codeSizeLongs);
            maximumCodeSizeLongs = Math.Max(maximumCodeSizeLongs, codeSizeLongs);
        }

        Dictionary<IAsmSymbol, MemoryAddress> addressBySymbol = [];
        Dictionary<ImageDescriptor, CogResourceLayout> layoutsByImage = [];
        Dictionary<StoragePlace, CogResourceLayout> layoutsByOwnedPlace = [];
        List<CogResourceLayout> layouts = [];

        foreach (ImagePlacementEntry placement in imagePlacement.Images)
        {
            IReadOnlyList<AsmAllocatedStorageDefinition> imageDefinitions = CollectImageCogDefinitions(module, placement.Image);
            Dictionary<IAsmSymbol, CogAddress> imageAddresses = AssignStableAddressesForImage(
                placement,
                imageDefinitions,
                codeSizeByImage[placement.Image],
                diagnostics);

            List<CogAddress> availableAddresses = BuildAvailableAddresses(
                placement.Image.ExecutionMode,
                codeSizeByImage[placement.Image],
                imageDefinitions,
                imageAddresses);
            List<IAsmSymbol> stableSymbols = imageDefinitions.Select(static definition => definition.Symbol).ToList();
            CogResourceLayout layout = new(placement, codeSizeByImage[placement.Image], availableAddresses, stableSymbols);
            layouts.Add(layout);
            layoutsByImage.Add(placement.Image, layout);

            foreach ((IAsmSymbol symbol, CogAddress virtualAddress) in imageAddresses)
            {
                addressBySymbol.Add(symbol, new MemoryAddress(placement.HubStartAddressBytes + ((int)virtualAddress * 4), new VirtualAddress(virtualAddress)));

                if (symbol is StoragePlace place && place.OwningImage is not null)
                    layoutsByOwnedPlace[place] = layout;
            }
        }

        CogResourceLayout entryLayout = layouts.Single(layout => ReferenceEquals(layout.Image, imagePlan.EntryImage));
        return new CogResourceLayoutSet(
            layouts,
            entryLayout,
            addressBySymbol,
            layoutsByImage,
            layoutsByOwnedPlace,
            maximumCodeSizeLongs);
    }

    private static IReadOnlyList<AsmAllocatedStorageDefinition> CollectImageCogDefinitions(AsmModule module, ImageDescriptor image)
    {
        List<AsmAllocatedStorageDefinition> definitions = [];
        foreach (AsmDataBlock block in module.DataBlocks)
        {
            if (block.Kind is not AsmDataBlockKind.Register and not AsmDataBlockKind.Constant)
                continue;

            foreach (AsmAllocatedStorageDefinition definition in block.Definitions.OfType<AsmAllocatedStorageDefinition>())
            {
                if (definition.StorageClass != AddressSpace.Cog)
                    continue;

                if (GetOwningImage(definition.Symbol) is not { } owningImage || !ReferenceEquals(owningImage, image))
                    continue;

                definitions.Add(definition);
            }
        }

        return definitions;
    }

    private static int CountImageCodeLongs(
        ImageDescriptor image,
        IReadOnlyList<AsmFunction> functions,
        bool includeDefaultBladeHalt)
    {
        int count = 0;
        foreach (AsmFunction function in functions)
        {
            foreach (AsmNode node in function.Nodes)
            {
                switch (node)
                {
                    case AsmInstructionNode:
                        count++;
                        break;
                    case AsmInlineDataNode inlineData:
                        count += GetInlineDataSizeLongs(inlineData);
                        break;
                }
            }
        }

        if (image.IsEntryImage && includeDefaultBladeHalt)
            count += DefaultBladeHaltInstructionCount;

        return count;
    }

    private static int GetInlineDataSizeLongs(AsmInlineDataNode inlineData)
    {
        int valueCount = Math.Max(1, inlineData.Values.Count);
        int totalBytes = inlineData.Directive switch
        {
            AsmDataDirective.Byte => valueCount,
            AsmDataDirective.Word => valueCount * 2,
            AsmDataDirective.Long => valueCount * 4,
            _ => Assert.UnreachableValue<int>(), // pragma: force-coverage
        };

        return (totalBytes + 3) / 4;
    }

    private static Dictionary<IAsmSymbol, CogAddress> AssignStableAddressesForImage(
        ImagePlacementEntry placement,
        IReadOnlyList<AsmAllocatedStorageDefinition> definitions,
        int codeSizeLongs,
        DiagnosticBag? diagnostics)
    {
        List<OccupiedRange> occupied = [new OccupiedRange(FirstSpecialRegisterAddress, 0x200)];
        if (placement.Image.ExecutionMode == AddressSpace.Cog && codeSizeLongs > 0)
            occupied.Add(new OccupiedRange(FirstNonSpecialAddress, codeSizeLongs));

        if (placement.Image.ExecutionMode == AddressSpace.Cog && codeSizeLongs > FirstSpecialRegisterAddress)
        {
            diagnostics?.ReportCogResourceLayoutFailed(
                placement.Image.Task.SourceSpan.Span,
                placement.Image.Task.Name,
                $"code uses '{codeSizeLongs.ToString(CultureInfo.InvariantCulture)}' longs before stable register data is placed");
        }

        Dictionary<IAsmSymbol, CogAddress> addresses = [];
        foreach (AsmAllocatedStorageDefinition definition in definitions.OrderBy(static definition => GetDeterministicKey(definition.Symbol), StringComparer.Ordinal))
        {
            if (!TryGetFixedAddress(definition, out CogAddress fixedAddress))
                continue;

            int size = GetCogDefinitionSizeLongs(definition);
            if (!TryReserve(addresses, occupied, definition.Symbol, (int)fixedAddress, size, diagnostics))
                continue;
        }

        IEnumerable<AsmAllocatedStorageDefinition> floatingDefinitions = definitions
            .Where(static definition => !TryGetFixedAddress(definition, out _))
            .OrderByDescending(GetCogDefinitionSizeLongs)
            .ThenBy(static definition => GetDeterministicKey(definition.Symbol), StringComparer.Ordinal);
        foreach (AsmAllocatedStorageDefinition definition in floatingDefinitions)
        {
            int size = GetCogDefinitionSizeLongs(definition);
            int alignment = GetCogDefinitionAlignmentLongs(definition);
            int? address = FindHighestFit(size, alignment, occupied);
            if (!address.HasValue)
            {
                diagnostics?.ReportCogResourceLayoutFailed(
                    GetOwnerSpan(definition.Symbol),
                    GetDeterministicKey(definition.Symbol),
                    $"stable COG storage of size '{size.ToString(CultureInfo.InvariantCulture)}' longs does not fit in image '{placement.Image.Task.Name}'");
                continue;
            }

            bool reserved = TryReserve(addresses, occupied, definition.Symbol, address.Value, size, diagnostics);
            Assert.Invariant(reserved, "Stable register placement must not overlap.");
        }

        return addresses;
    }

    private static List<CogAddress> BuildAvailableAddresses(
        AddressSpace executionMode,
        int codeSizeLongs,
        IReadOnlyList<AsmAllocatedStorageDefinition> definitions,
        IReadOnlyDictionary<IAsmSymbol, CogAddress> imageAddresses)
    {
        HashSet<int> reservedAddresses = [];
        if (executionMode == AddressSpace.Cog)
        {
            for (int address = 0; address < codeSizeLongs; address++)
                reservedAddresses.Add(address);
        }

        for (int address = FirstSpecialRegisterAddress; address < 0x200; address++)
            reservedAddresses.Add(address);

        foreach (AsmAllocatedStorageDefinition definition in definitions)
        {
            if (!imageAddresses.TryGetValue(definition.Symbol, out CogAddress startAddress))
                continue;

            int size = GetCogDefinitionSizeLongs(definition);
            for (int address = (int)startAddress; address < (int)startAddress + size; address++)
                reservedAddresses.Add(address);
        }

        List<CogAddress> availableAddresses = [];
        for (int address = FirstNonSpecialAddress; address <= LastAllocatableAddress; address++)
        {
            if (!reservedAddresses.Contains(address))
                availableAddresses.Add(new CogAddress(address));
        }

        availableAddresses.Sort(static (left, right) => right.CompareTo(left));
        return availableAddresses;
    }

    private static ImageDescriptor? GetOwningImage(IAsmSymbol symbol)
    {
        return symbol switch
        {
            StoragePlace place => place.OwningImage,
            AsmSharedConstantSymbol constant => constant.Image,
            AsmSpillSlotSymbol spill => spill.Image,
            _ => null,
        };
    }

    private static bool TryReserve(
        IDictionary<IAsmSymbol, CogAddress> addresses,
        ICollection<OccupiedRange> occupied,
        IAsmSymbol symbol,
        int startAddress,
        int sizeLongs,
        DiagnosticBag? diagnostics)
    {
        Requires.NotNull(symbol);
        Requires.NonNegative(startAddress);
        Requires.Positive(sizeLongs);

        int endAddressExclusive = checked(startAddress + sizeLongs);
        if (startAddress < FirstNonSpecialAddress
            || endAddressExclusive > 0x200
            || (startAddress < FirstSpecialRegisterAddress && endAddressExclusive > FirstSpecialRegisterAddress))
        {
            diagnostics?.ReportCogResourceLayoutFailed(
                GetOwnerSpan(symbol),
                GetDeterministicKey(symbol),
                $"address range '${startAddress:X3}..${(endAddressExclusive - 1):X3}' overlaps the reserved special-register tail");
            return false;
        }

        foreach (OccupiedRange range in occupied)
        {
            if (!RangesOverlap(startAddress, endAddressExclusive, range.StartAddress, range.EndAddressExclusive))
                continue;

            diagnostics?.ReportCogResourceLayoutFailed(
                GetOwnerSpan(symbol),
                GetDeterministicKey(symbol),
                $"address range '${startAddress:X3}..${(endAddressExclusive - 1):X3}' overlaps already reserved register space");
            return false;
        }

        addresses.Add(symbol, new CogAddress(startAddress));
        occupied.Add(new OccupiedRange(startAddress, endAddressExclusive));
        return true;
    }

    private static bool TryGetFixedAddress(AsmAllocatedStorageDefinition definition, out CogAddress address)
    {
        Requires.NotNull(definition);

        if (definition.Symbol is StoragePlace place)
        {
            if (place.StorageClass != AddressSpace.Cog)
            {
                address = default;
                return false;
            }

            if (place.ResolvedLayoutSlot is LayoutSlot layoutSlot && layoutSlot.StorageClass == AddressSpace.Cog)
            {
                address = layoutSlot.Address.ToCogAddress();
                return true;
            }

            if (place.FixedAddress is VirtualAddress fixedAddress)
            {
                address = fixedAddress.ToCogAddress();
                return true;
            }
        }

        address = default;
        return false;
    }

    private static int GetCogDefinitionSizeLongs(AsmAllocatedStorageDefinition definition)
    {
        Requires.NotNull(definition);
        Assert.Invariant(definition.StorageClass == AddressSpace.Cog, "COG planner only accepts COG-backed definitions.");
        return definition.Count;
    }

    private static int GetCogDefinitionAlignmentLongs(AsmAllocatedStorageDefinition definition)
    {
        Requires.NotNull(definition);
        return Math.Max(1, (definition.AlignmentBytes + 3) / 4);
    }

    private static int? FindHighestFit(int sizeLongs, int alignmentLongs, IReadOnlyCollection<OccupiedRange> occupied)
    {
        Requires.Positive(sizeLongs);
        Requires.Positive(alignmentLongs);

        int address = LastAllocatableAddress - sizeLongs + 1;
        while (address >= FirstNonSpecialAddress)
        {
            address = AlignDown(address, alignmentLongs);
            if (address < FirstNonSpecialAddress)
                return null;

            int endAddressExclusive = checked(address + sizeLongs);
            OccupiedRange overlap = occupied
                .OrderByDescending(static range => range.StartAddress)
                .FirstOrDefault(range => RangesOverlap(address, endAddressExclusive, range.StartAddress, range.EndAddressExclusive));

            if (overlap == default)
                return address;

            address = overlap.StartAddress - sizeLongs;
        }

        return null;
    }

    private static int AlignDown(int value, int alignment)
    {
        Requires.NonNegative(value);
        Requires.Positive(alignment);
        int remainder = value % alignment;
        return remainder == 0 ? value : checked(value - remainder);
    }

    private static bool RangesOverlap(int startA, int endA, int startB, int endB)
    {
        return startA < endB && startB < endA;
    }

    private static string GetDeterministicKey(IAsmSymbol symbol)
    {
        return symbol switch
        {
            StoragePlace place => $"{place.OwningImage?.Task.Name ?? "shared"}:{place.EmittedName}",
            AsmSharedConstantSymbol constant => $"{constant.Image.Task.Name}:{constant.Name}",
            AsmSpillSlotSymbol spill => $"{spill.Image.Task.Name}:{spill.Name}",
            _ => symbol.Name,
        };
    }

    private static TextSpan GetOwnerSpan(IAsmSymbol symbol)
    {
        return symbol switch
        {
            StoragePlace place => place.Symbol.SourceSpan.Span,
            _ => TextSpan.FromBounds(0, 0),
        };
    }
}
