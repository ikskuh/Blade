using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Blade.Diagnostics;
using Blade.IR.Asm;
using Blade.Semantics;
using Blade.Source;

namespace Blade.IR;

/// <summary>
/// Represents the image-specific COG occupancy that remains after stable COG-backed data
/// has been assigned concrete addresses. Each image starts code at <c>$000</c>, reserves
/// the special-register tail at <c>$1F0..$1FF</c>, and exposes the remaining concrete
/// allocatable register addresses for image-local ABI and virtual-register allocation.
/// </summary>
public sealed class CogResourceLayout(
    ImageDescriptor image,
    int codeSizeLongs,
    IReadOnlyList<int> availableRegisterAddresses,
    IReadOnlyList<IAsmSymbol> referencedStableSymbols)
{
    private readonly HashSet<int> _availableRegisterAddresses = [.. availableRegisterAddresses];
    private readonly HashSet<IAsmSymbol> _referencedStableSymbols = [.. referencedStableSymbols];

    /// <summary>
    /// Gets the logical image whose COG resources are being described.
    /// </summary>
    public ImageDescriptor Image { get; } = Requires.NotNull(image);

    /// <summary>
    /// Gets the number of COG longs occupied by executable code that starts at <c>$000</c>.
    /// </summary>
    public int CodeSizeLongs { get; } = Requires.NonNegative(codeSizeLongs);

    /// <summary>
    /// Gets the concrete allocatable COG addresses that remain free after reserving code,
    /// stable data, and the special-register tail for this image.
    /// </summary>
    public IReadOnlyList<int> AvailableRegisterAddresses { get; } = Requires.NotNull(availableRegisterAddresses);

    /// <summary>
    /// Gets the stable COG-backed symbols that this image physically contains.
    /// </summary>
    public IReadOnlyList<IAsmSymbol> ReferencedStableSymbols { get; } = Requires.NotNull(referencedStableSymbols);

    /// <summary>
    /// Returns whether the supplied concrete COG address is still free for image-local allocation.
    /// </summary>
    public bool IsRegisterAddressAvailable(int address)
    {
        Requires.InRange(address, 0, 0x1EF);
        return _availableRegisterAddresses.Contains(address);
    }

    /// <summary>
    /// Returns whether the supplied stable COG symbol is physically present in this image.
    /// </summary>
    public bool ContainsStableSymbol(IAsmSymbol symbol)
    {
        Requires.NotNull(symbol);
        return _referencedStableSymbols.Contains(symbol);
    }
}

/// <summary>
/// Represents the stable COG-backed data addresses plus the per-image free-register sets
/// derived from the legalized ASMIR. Stable data is assigned once for the whole program so
/// every emitted symbol keeps a concrete COG address, while each image still gets its own
/// code size and therefore its own remaining allocatable register pool.
/// </summary>
public sealed class CogResourceLayoutSet
{
    private readonly IReadOnlyDictionary<IAsmSymbol, int> _stableAddressesBySymbol;
    private readonly IReadOnlyDictionary<FunctionSymbol, CogResourceLayout> _layoutsByFunction;
    private readonly IReadOnlyDictionary<StoragePlace, CogResourceLayout> _layoutsByOwnedPlace;

    public CogResourceLayoutSet(
        IReadOnlyList<CogResourceLayout> images,
        CogResourceLayout entryImage,
        IReadOnlyDictionary<IAsmSymbol, int> stableAddressesBySymbol,
        IReadOnlyDictionary<FunctionSymbol, CogResourceLayout> layoutsByFunction,
        IReadOnlyDictionary<StoragePlace, CogResourceLayout> layoutsByOwnedPlace,
        int maximumCodeSizeLongs)
    {
        Requires.NotNull(images);
        Requires.NotNull(entryImage);
        Requires.NotNull(stableAddressesBySymbol);
        Requires.NotNull(layoutsByFunction);
        Requires.NotNull(layoutsByOwnedPlace);
        Requires.That(images.Contains(entryImage));

        Images = images;
        EntryImage = entryImage;
        MaximumCodeSizeLongs = Requires.NonNegative(maximumCodeSizeLongs);
        _stableAddressesBySymbol = stableAddressesBySymbol;
        _layoutsByFunction = layoutsByFunction;
        _layoutsByOwnedPlace = layoutsByOwnedPlace;
    }

    /// <summary>
    /// Gets the per-image COG layouts in image-plan order.
    /// </summary>
    public IReadOnlyList<CogResourceLayout> Images { get; }

    /// <summary>
    /// Gets the COG layout for the entry image.
    /// </summary>
    public CogResourceLayout EntryImage { get; }

    /// <summary>
    /// Gets the largest per-image code footprint in longs. Stable COG-backed data is
    /// assigned above this floor so it cannot collide with any image's code.
    /// </summary>
    public int MaximumCodeSizeLongs { get; }

    /// <summary>
    /// Tries to get the stable concrete COG address for one emitted COG-backed symbol.
    /// </summary>
    public bool TryGetStableAddress(IAsmSymbol symbol, out int address)
    {
        Requires.NotNull(symbol);
        return _stableAddressesBySymbol.TryGetValue(symbol, out address);
    }

    /// <summary>
    /// Tries to get the per-image COG layout that owns one lowered function.
    /// </summary>
    public bool TryGetLayout(FunctionSymbol function, out CogResourceLayout? layout)
    {
        Requires.NotNull(function);
        return _layoutsByFunction.TryGetValue(function, out layout);
    }

    /// <summary>
    /// Tries to get the per-image COG layout that owns one image-local storage place.
    /// Stable globals may appear in multiple images and therefore do not participate here.
    /// </summary>
    public bool TryGetOwningLayout(StoragePlace place, out CogResourceLayout? layout)
    {
        Requires.NotNull(place);
        return _layoutsByOwnedPlace.TryGetValue(place, out layout);
    }
}

/// <summary>
/// Builds stable COG-backed data addresses and the per-image free-register pools that the
/// register allocator must honor. Stable data is packed from the back of the allocatable
/// COG range so the front remains available for code growing upward from <c>$000</c>.
/// </summary>
public static class CogResourcePlanner
{
    private const int FirstSpecialRegisterAddress = 0x1F0;
    private const int FirstNonSpecialAddress = 0x000;
    private const int LastAllocatableAddress = 0x1EF;
    private const int DefaultBladeHaltInstructionCount = 2;

    private readonly record struct OccupiedRange(int StartAddress, int EndAddressExclusive);

    /// <summary>
    /// Builds the stable COG-backed data map and the per-image free-register sets after
    /// instruction-count-changing prepasses have established concrete code sizes.
    /// </summary>
    public static CogResourceLayoutSet Build(
        AsmModule module,
        ImagePlan imagePlan,
        LayoutSolution layoutSolution,
        bool includeDefaultBladeHalt,
        DiagnosticBag? diagnostics)
    {
        Requires.NotNull(module);
        Requires.NotNull(imagePlan);
        Requires.NotNull(layoutSolution);

        Dictionary<FunctionSymbol, AsmFunction> functionsBySymbol = module.Functions.ToDictionary(static function => function.Symbol);
        Dictionary<ImageDescriptor, int> codeSizeByImage = [];
        int maximumCodeSizeLongs = 0;
        foreach (ImageDescriptor image in imagePlan.Images)
        {
            int codeSizeLongs = CountImageCodeLongs(image, functionsBySymbol, includeDefaultBladeHalt);
            codeSizeByImage.Add(image, codeSizeLongs);
            maximumCodeSizeLongs = Math.Max(maximumCodeSizeLongs, codeSizeLongs);
        }

        bool maxCodeFits = maximumCodeSizeLongs <= FirstSpecialRegisterAddress;
        if (!maxCodeFits)
        {
            ImageDescriptor entryImage = imagePlan.EntryImage;
            diagnostics?.ReportCogResourceLayoutFailed(
                entryImage.Task.SourceSpan.Span,
                entryImage.Task.Name,
                $"code uses '{maximumCodeSizeLongs.ToString(CultureInfo.InvariantCulture)}' longs before stable data is placed");
        }

        IReadOnlyList<AsmAllocatedStorageDefinition> stableCogDefinitions = CollectStableCogDefinitions(module);
        Dictionary<IAsmSymbol, int> stableAddressesBySymbol = AssignStableAddresses(
            stableCogDefinitions,
            maximumCodeSizeLongs,
            diagnostics);

        List<CogResourceLayout> imageLayouts = [];
        Dictionary<FunctionSymbol, CogResourceLayout> layoutsByFunction = [];
        Dictionary<StoragePlace, CogResourceLayout> layoutsByOwnedPlace = [];
        foreach (ImageDescriptor image in imagePlan.Images)
        {
            CogResourceLayout layout = BuildImageLayout(
                image,
                codeSizeByImage[image],
                stableAddressesBySymbol,
                stableCogDefinitions,
                functionsBySymbol);
            imageLayouts.Add(layout);

            foreach (FunctionSymbol function in image.Functions)
                layoutsByFunction[function] = layout;

            foreach (StoragePlace place in CollectImageLocalOwnedPlaces(image, functionsBySymbol))
                layoutsByOwnedPlace[place] = layout;
        }

        CogResourceLayout entryLayout = imageLayouts.Single(layout => layout.Image.IsEntryImage);
        return new CogResourceLayoutSet(
            imageLayouts,
            entryLayout,
            stableAddressesBySymbol,
            layoutsByFunction,
            layoutsByOwnedPlace,
            maximumCodeSizeLongs);
    }

    private static IReadOnlyList<AsmAllocatedStorageDefinition> CollectStableCogDefinitions(AsmModule module)
    {
        List<AsmAllocatedStorageDefinition> definitions = [];
        foreach (AsmDataBlock block in module.DataBlocks)
        {
            if (block.Kind is not AsmDataBlockKind.Register and not AsmDataBlockKind.Constant)
                continue;

            foreach (AsmAllocatedStorageDefinition definition in block.Definitions.OfType<AsmAllocatedStorageDefinition>())
            {
                if (definition.StorageClass == VariableStorageClass.Cog)
                    definitions.Add(definition);
            }
        }

        return definitions;
    }

    private static int CountImageCodeLongs(
        ImageDescriptor image,
        IReadOnlyDictionary<FunctionSymbol, AsmFunction> functionsBySymbol,
        bool includeDefaultBladeHalt)
    {
        int count = 0;
        foreach (FunctionSymbol function in image.Functions)
        {
            if (!functionsBySymbol.TryGetValue(function, out AsmFunction? asmFunction))
                continue;

            foreach (AsmNode node in asmFunction.Nodes)
            {
                if (node is AsmInstructionNode)
                    count++;
            }
        }

        if (image.IsEntryImage && includeDefaultBladeHalt)
            count += DefaultBladeHaltInstructionCount;

        return count;
    }

    private static Dictionary<IAsmSymbol, int> AssignStableAddresses(
        IReadOnlyList<AsmAllocatedStorageDefinition> definitions,
        int maximumCodeSizeLongs,
        DiagnosticBag? diagnostics)
    {
        List<OccupiedRange> occupied = [];
        occupied.Add(new OccupiedRange(FirstSpecialRegisterAddress, 0x200));
        if (maximumCodeSizeLongs > 0)
            occupied.Add(new OccupiedRange(FirstNonSpecialAddress, maximumCodeSizeLongs));

        Dictionary<IAsmSymbol, int> addresses = [];
        foreach (AsmAllocatedStorageDefinition definition in definitions.OrderBy(static definition => GetDeterministicKey(definition.Symbol), StringComparer.Ordinal))
        {
            if (!TryGetFixedAddress(definition, out int fixedAddress))
                continue;

            int size = GetCogDefinitionSizeLongs(definition);
            if (!TryReserve(addresses, occupied, definition.Symbol, fixedAddress, size, diagnostics))
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
                TextSpan span = GetOwnerSpan(definition.Symbol);
                diagnostics?.ReportCogResourceLayoutFailed(
                    span,
                    GetDeterministicKey(definition.Symbol),
                    $"stable COG storage of size '{size.ToString(CultureInfo.InvariantCulture)}' longs does not fit above code and below '$1F0'");
                continue;
            }

            bool reserved = TryReserve(addresses, occupied, definition.Symbol, address.Value, size, diagnostics);
            Assert.Invariant(reserved, "Back-to-front stable COG placement must not overlap previously reserved ranges.");
        }

        return addresses;
    }

    private static CogResourceLayout BuildImageLayout(
        ImageDescriptor image,
        int codeSizeLongs,
        IReadOnlyDictionary<IAsmSymbol, int> stableAddressesBySymbol,
        IReadOnlyList<AsmAllocatedStorageDefinition> stableDefinitions,
        IReadOnlyDictionary<FunctionSymbol, AsmFunction> functionsBySymbol)
    {
        HashSet<IAsmSymbol> referencedStableSymbols = [];
        HashSet<StoragePlace> storagePlaces = CollectStoragePlacesForImage(image, functionsBySymbol);
        HashSet<IAsmSymbol> referencedAsmSymbols = CollectReferencedAsmSymbols(image, functionsBySymbol);

        foreach (AsmAllocatedStorageDefinition definition in stableDefinitions)
        {
            if (definition.Symbol is StoragePlace place)
            {
                bool referencedByStorage = image.Storage.Contains(place.Symbol) || storagePlaces.Contains(place);
                bool referencedByAsm = referencedAsmSymbols.Contains(place);
                if (!referencedByStorage && !referencedByAsm)
                    continue;
            }
            else if (!referencedAsmSymbols.Contains(definition.Symbol))
            {
                continue;
            }

            referencedStableSymbols.Add(definition.Symbol);
        }

        HashSet<int> reservedAddresses = [];
        for (int address = 0; address < codeSizeLongs; address++)
            reservedAddresses.Add(address);
        for (int address = FirstSpecialRegisterAddress; address < 0x200; address++)
            reservedAddresses.Add(address);

        foreach (IAsmSymbol symbol in referencedStableSymbols)
        {
            AsmAllocatedStorageDefinition definition = stableDefinitions.Single(candidate => ReferenceEquals(candidate.Symbol, symbol));
            if (stableAddressesBySymbol.TryGetValue(symbol, out int startAddress))
            {
                int size = GetCogDefinitionSizeLongs(definition);
                for (int address = startAddress; address < startAddress + size; address++)
                    reservedAddresses.Add(address);
            }
        }

        foreach ((IAsmSymbol symbol, int startAddress) in stableAddressesBySymbol)
        {
            AsmAllocatedStorageDefinition definition = stableDefinitions.Single(candidate => ReferenceEquals(candidate.Symbol, symbol));
            int size = GetCogDefinitionSizeLongs(definition);
            for (int address = startAddress; address < startAddress + size; address++)
                reservedAddresses.Add(address);
        }

        List<int> availableAddresses = [];
        for (int address = FirstNonSpecialAddress; address <= LastAllocatableAddress; address++)
        {
            if (!reservedAddresses.Contains(address))
                availableAddresses.Add(address);
        }

        availableAddresses.Sort(static (left, right) => right.CompareTo(left));
        return new CogResourceLayout(image, codeSizeLongs, availableAddresses, [.. referencedStableSymbols]);
    }

    private static HashSet<StoragePlace> CollectStoragePlacesForImage(
        ImageDescriptor image,
        IReadOnlyDictionary<FunctionSymbol, AsmFunction> functionsBySymbol)
    {
        HashSet<StoragePlace> places = [];
        HashSet<IAsmSymbol> referenced = CollectReferencedAsmSymbols(image, functionsBySymbol);
        foreach (IAsmSymbol symbol in referenced)
        {
            if (symbol is StoragePlace place)
                places.Add(place);
        }

        return places;
    }

    private static HashSet<IAsmSymbol> CollectReferencedAsmSymbols(
        ImageDescriptor image,
        IReadOnlyDictionary<FunctionSymbol, AsmFunction> functionsBySymbol)
    {
        HashSet<IAsmSymbol> symbols = [];
        foreach (FunctionSymbol function in image.Functions)
        {
            if (!functionsBySymbol.TryGetValue(function, out AsmFunction? asmFunction))
                continue;

            foreach (AsmNode node in asmFunction.Nodes)
            {
                if (node is not AsmInstructionNode instruction)
                    continue;

                foreach (AsmOperand operand in instruction.Operands)
                {
                    if (operand is AsmSymbolOperand symbolOperand)
                        symbols.Add(symbolOperand.Symbol);
                }
            }
        }

        return symbols;
    }

    private static IReadOnlyList<StoragePlace> CollectImageLocalOwnedPlaces(
        ImageDescriptor image,
        IReadOnlyDictionary<FunctionSymbol, AsmFunction> functionsBySymbol)
    {
        HashSet<StoragePlace> places = [];
        foreach (IAsmSymbol symbol in CollectReferencedAsmSymbols(image, functionsBySymbol))
        {
            if (symbol is not StoragePlace place)
                continue;

            if (place.RegisterRole is StoragePlaceRegisterRole.InternalDedicated or StoragePlaceRegisterRole.InternalShared)
                places.Add(place);
        }

        return [.. places];
    }

    private static bool TryReserve(
        IDictionary<IAsmSymbol, int> addresses,
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
                $"address range '${startAddress:X3}..${(endAddressExclusive - 1):X3}' overlaps already reserved COG space");
            return false;
        }

        addresses.Add(symbol, startAddress);
        occupied.Add(new OccupiedRange(startAddress, endAddressExclusive));
        return true;
    }

    private static bool TryGetFixedAddress(AsmAllocatedStorageDefinition definition, out int address)
    {
        Requires.NotNull(definition);

        if (definition.Symbol is StoragePlace place)
        {
            if (place.StorageClass != VariableStorageClass.Cog)
            {
                address = 0;
                return false;
            }

            if (place.ResolvedLayoutSlot is LayoutSlot layoutSlot && layoutSlot.StorageClass == VariableStorageClass.Cog)
            {
                address = layoutSlot.Address;
                return true;
            }

            if (place.FixedAddress is int fixedAddress)
            {
                address = fixedAddress;
                return true;
            }
        }

        address = 0;
        return false;
    }

    private static int GetCogDefinitionSizeLongs(AsmAllocatedStorageDefinition definition)
    {
        Requires.NotNull(definition);
        Assert.Invariant(definition.StorageClass == VariableStorageClass.Cog, "COG layout only accepts COG-backed data definitions.");
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
            StoragePlace place => place.EmittedName,
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
