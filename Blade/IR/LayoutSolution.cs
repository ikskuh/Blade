using System.Collections.Generic;
using System.Linq;
using Blade.Diagnostics;
using Blade.Semantics;
using Blade.Semantics.Bound;

namespace Blade.IR;

/// <summary>
/// Represents the program-wide solved addresses for stable layout-backed storage.
/// A layout solution is shared across every image so that a given layout member keeps the
/// same physical address whenever that layout is imported. The solver owns only stable
/// layout-member addresses; image-local COG occupancy that depends on final code size is
/// modeled later by the per-image COG resource layout.
/// </summary>
public sealed class LayoutSolution
{
    private readonly IReadOnlyDictionary<GlobalVariableSymbol, LayoutSlot> _slotsBySymbol;

    public LayoutSolution(IReadOnlyList<LayoutSlot> slots)
    {
        Requires.NotNull(slots);

        Slots = slots;
        Dictionary<GlobalVariableSymbol, LayoutSlot> slotsBySymbol = [];
        foreach (LayoutSlot slot in slots)
            slotsBySymbol.Add(slot.Symbol, slot);
        _slotsBySymbol = slotsBySymbol;
    }

    /// <summary>
    /// Gets every concretely solved layout slot in the program.
    /// </summary>
    public IReadOnlyList<LayoutSlot> Slots { get; }

    /// <summary>
    /// Tries to get the solved slot for one layout-backed variable.
    /// </summary>
    public bool TryGetSlot(GlobalVariableSymbol symbol, out LayoutSlot? slot)
    {
        Requires.NotNull(symbol);
        return _slotsBySymbol.TryGetValue(symbol, out slot);
    }
}

/// <summary>
/// Represents one solved layout-backed storage slot in a concrete memory space.
/// </summary>
public sealed class LayoutSlot(
    GlobalVariableSymbol symbol,
    LayoutSymbol layout,
    VirtualAddress address,
    int sizeInAddressUnits,
    int alignmentInAddressUnits)
{
    /// <summary>
    /// Gets the layout member symbol that owns this slot.
    /// </summary>
    public GlobalVariableSymbol Symbol { get; } = Requires.NotNull(symbol);

    /// <summary>
    /// Gets the layout that directly declared the member.
    /// </summary>
    public LayoutSymbol Layout { get; } = Requires.NotNull(layout);

    /// <summary>
    /// Gets the storage space in which the slot lives.
    /// </summary>
    public AddressSpace StorageClass { get; } = symbol.StorageClass;

    /// <summary>
    /// Gets the resolved start address in storage-space units.
    /// Hub addresses are bytes; LUT addresses are long slots.
    /// </summary>
    public VirtualAddress Address { get; } = address;

    /// <summary>
    /// Gets the occupied size in storage-space units.
    /// </summary>
    public int SizeInAddressUnits { get; } = Requires.Positive(sizeInAddressUnits);

    /// <summary>
    /// Gets the required alignment in storage-space units.
    /// </summary>
    public int AlignmentInAddressUnits { get; } = Requires.Positive(alignmentInAddressUnits);

    /// <summary>
    /// Gets the exclusive end address of the slot.
    /// </summary>
    public int EndAddressExclusive
    {
        get
        {
            (_, int rawAddress) = Address.GetDataAddress();
            return checked(rawAddress + SizeInAddressUnits);
        }
    }
}

/// <summary>
/// Solves stable storage addresses for layout-backed variables after image placement.
/// </summary>
public static class LayoutSolver
{
    private const int FirstSpecialCogAddress = 0x1F0;
    private static readonly int CogAddressSpaceSize = AddressSpace.Cog.GetAddressUnitCount();
    private static readonly int LutAddressSpaceSize = AddressSpace.Lut.GetAddressUnitCount();

    private readonly record struct LayoutCandidate(
        GlobalVariableSymbol Symbol,
        LayoutSymbol Layout,
        StorageLayoutShape Shape,
        int AlignmentInAddressUnits);

    private readonly record struct OccupiedAddressRange(HubAddress StartAddress, HubAddress EndAddressExclusive);

    /// <summary>
    /// Solves the program's stable layout-backed storage after image placement has reserved
    /// hub-memory ranges for every required image.
    /// </summary>
    public static LayoutSolution SolveStableLayouts(BoundProgram program, ImagePlacement imagePlacement, DiagnosticBag? diagnostics = null)
    {
        Requires.NotNull(program);
        Requires.NotNull(imagePlacement);

        IReadOnlyList<LayoutSymbol> layouts = CollectLayouts(imagePlacement);
        List<LayoutSlot> slots = [];
        slots.AddRange(SolveStorageClass(layouts, AddressSpace.Cog, [], diagnostics));
        slots.AddRange(SolveStorageClass(layouts, AddressSpace.Lut, [], diagnostics));
        slots.AddRange(SolveStorageClass(layouts, AddressSpace.Hub, CreateReservedHubRanges(imagePlacement), diagnostics));
        return new LayoutSolution(slots);
    }

    private static IReadOnlyList<LayoutSlot> SolveStorageClass(
        IReadOnlyList<LayoutSymbol> layouts,
        AddressSpace storageClass,
        IReadOnlyList<OccupiedAddressRange> reservedRanges,
        DiagnosticBag? diagnostics)
    {
        List<LayoutCandidate> candidates = CollectCandidates(layouts, storageClass);
        if (candidates.Count == 0)
            return [];

        List<LayoutSlot> slots = [];
        List<LayoutSlot> occupied = [];
        foreach (LayoutCandidate candidate in candidates.Where(static candidate => candidate.Symbol.FixedAddress.HasValue))
        {
            VirtualAddress fixedAddress = candidate.Symbol.FixedAddress
                ?? Assert.UnreachableValue<VirtualAddress>(); // pragma: force-coverage

            if (!TryValidateAlignment(candidate, diagnostics))
                continue;

            if (!TryValidateFixedAddress(candidate, fixedAddress, reservedRanges, diagnostics))
                continue;

            LayoutSlot slot = new(
                candidate.Symbol,
                candidate.Layout,
                fixedAddress,
                candidate.Shape.SizeInAddressUnits,
                candidate.AlignmentInAddressUnits);
            if (!TryAddOccupiedSlot(slot, occupied, diagnostics))
                continue;

            slots.Add(slot);
        }

        IEnumerable<LayoutCandidate> floatingCandidates = storageClass == AddressSpace.Cog
            ? candidates.Where(static candidate => !candidate.Symbol.FixedAddress.HasValue)
                .OrderByDescending(static candidate => candidate.Shape.SizeInAddressUnits)
                .ThenBy(static candidate => candidate.Layout.Name, System.StringComparer.Ordinal)
                .ThenBy(static candidate => candidate.Symbol.Name, System.StringComparer.Ordinal)
            : candidates.Where(static candidate => !candidate.Symbol.FixedAddress.HasValue);

        foreach (LayoutCandidate candidate in floatingCandidates)
        {
            if (!TryValidateAlignment(candidate, diagnostics))
                continue;

            VirtualAddress? address = candidate.Symbol.StorageClass == AddressSpace.Cog
                ? FindHighestFitAddress(candidate, occupied)
                : FindFirstFitAddress(candidate, occupied, reservedRanges);
            if (!address.HasValue)
            {
                diagnostics?.ReportLayoutAllocationFailed(
                    candidate.Symbol.SourceSpan.Span,
                    LayoutDebugNameFormatter.FormatLayoutName(candidate.Layout),
                    candidate.Symbol.Name,
                    candidate.Symbol.StorageClass,
                    candidate.Shape.SizeInAddressUnits,
                    candidate.AlignmentInAddressUnits);
                continue;
            }

            LayoutSlot slot = new(
                candidate.Symbol,
                candidate.Layout,
                address.Value,
                candidate.Shape.SizeInAddressUnits,
                candidate.AlignmentInAddressUnits);
            bool added = TryAddOccupiedSlot(slot, occupied, diagnostics);
            Assert.Invariant(added, "First-fit placement must not produce an overlapping slot.");
            slots.Add(slot);
        }

        return slots;
    }

    private static IReadOnlyList<OccupiedAddressRange> CreateReservedHubRanges(ImagePlacement imagePlacement)
    {
        Requires.NotNull(imagePlacement);

        List<OccupiedAddressRange> ranges = [];
        foreach (ImagePlacementEntry placement in imagePlacement.Images)
        {
            Assert.Invariant(
                placement.SizeBytes == ImagePlacer.ReservedImageSizeBytes,
                "The current image placement stage must reserve a uniform provisional image size.");
            ranges.Add(new OccupiedAddressRange(placement.HubStartAddressBytes, placement.HubEndAddressExclusive));
        }

        return ranges;
    }

    private static List<LayoutCandidate> CollectCandidates(
        IReadOnlyList<LayoutSymbol> layouts,
        AddressSpace storageClass)
    {
        List<LayoutCandidate> candidates = [];
        foreach (LayoutSymbol layout in layouts.OrderBy(static layout => layout.Name, System.StringComparer.Ordinal))
        {
            foreach (GlobalVariableSymbol symbol in layout.DeclaredMembers.Values.OrderBy(static member => member.Name, System.StringComparer.Ordinal))
            {
                if (symbol.StorageClass != storageClass)
                    continue;

                if (symbol.IsExtern && !symbol.FixedAddress.HasValue)
                    continue;

                StorageLayoutShape shape = StorageLayoutShape.FromVariable(symbol);
                int alignment = symbol.Alignment ?? shape.DefaultAlignmentInAddressUnits;
                candidates.Add(new LayoutCandidate(symbol, layout, shape, alignment));
            }
        }

        return candidates;
    }

    private static IReadOnlyList<LayoutSymbol> CollectLayouts(ImagePlacement imagePlacement)
    {
        HashSet<LayoutSymbol> layouts = [];

        foreach (ImagePlacementEntry placement in imagePlacement.Images)
        {
            foreach (FunctionSymbol function in placement.Image.Functions)
            {
                if (function.ImplicitLayout is LayoutSymbol implicitLayout)
                    CollectLayoutTree(implicitLayout, layouts);

                foreach (LayoutSymbol associatedLayout in function.AssociatedLayouts)
                    CollectLayoutTree(associatedLayout, layouts);
            }

            CollectLayoutTree(placement.Image.Task, layouts);
            if (placement.Image.EntryFunction.ImplicitLayout is LayoutSymbol entryImplicitLayout)
                CollectLayoutTree(entryImplicitLayout, layouts);

            foreach (LayoutSymbol associatedLayout in placement.Image.EntryFunction.AssociatedLayouts)
                CollectLayoutTree(associatedLayout, layouts);
        }

        return layouts.ToList();
    }

    private static void CollectLayoutTree(LayoutSymbol layout, ISet<LayoutSymbol> seen)
    {
        if (!seen.Add(layout))
            return;

        foreach (LayoutSymbol parent in layout.Parents)
            CollectLayoutTree(parent, seen);
    }

    private static bool TryValidateAlignment(LayoutCandidate candidate, DiagnosticBag? diagnostics)
    {
        if (candidate.AlignmentInAddressUnits > 0
            && (candidate.AlignmentInAddressUnits & (candidate.AlignmentInAddressUnits - 1)) == 0)
        {
            return true;
        }

        if (diagnostics is not null)
        {
            diagnostics.Report(new InvalidLayoutAlignmentError(
                diagnostics.CurrentSource,
                candidate.Symbol.SourceSpan.Span,
                LayoutDebugNameFormatter.FormatLayoutName(candidate.Layout),
                candidate.Symbol.Name,
                candidate.AlignmentInAddressUnits));
        }

        return false;
    }

    private static bool TryValidateFixedAddress(
        LayoutCandidate candidate,
        VirtualAddress fixedAddress,
        IReadOnlyList<OccupiedAddressRange> reservedRanges,
        DiagnosticBag? diagnostics)
    {
        int rawAddress = GetRawAddress(fixedAddress);
        if (rawAddress % candidate.AlignmentInAddressUnits != 0)
        {
            diagnostics?.ReportInvalidLayoutAddress(
                candidate.Symbol.SourceSpan.Span,
                LayoutDebugNameFormatter.FormatLayoutName(candidate.Layout),
                candidate.Symbol.Name,
                candidate.Symbol.StorageClass,
                rawAddress,
                candidate.Shape.SizeInAddressUnits);
            return false;
        }

        if (candidate.Symbol.StorageClass == AddressSpace.Lut
            && rawAddress + candidate.Shape.SizeInAddressUnits > LutAddressSpaceSize)
        {
            diagnostics?.ReportInvalidLayoutAddress(
                candidate.Symbol.SourceSpan.Span,
                LayoutDebugNameFormatter.FormatLayoutName(candidate.Layout),
                candidate.Symbol.Name,
                candidate.Symbol.StorageClass,
                rawAddress,
                candidate.Shape.SizeInAddressUnits);
            return false;
        }

        if (candidate.Symbol.StorageClass == AddressSpace.Cog)
        {
            int endAddress = checked(rawAddress + candidate.Shape.SizeInAddressUnits);
            if (endAddress > CogAddressSpaceSize)
            {
                diagnostics?.ReportInvalidLayoutAddress(
                    candidate.Symbol.SourceSpan.Span,
                    LayoutDebugNameFormatter.FormatLayoutName(candidate.Layout),
                    candidate.Symbol.Name,
                    candidate.Symbol.StorageClass,
                    rawAddress,
                    candidate.Shape.SizeInAddressUnits);
                return false;
            }

            bool overlapsSpecialTail = rawAddress < CogAddressSpaceSize
                && endAddress > FirstSpecialCogAddress;
            if (overlapsSpecialTail && !candidate.Symbol.IsExtern)
            {
                diagnostics?.ReportInvalidLayoutAddress(
                    candidate.Symbol.SourceSpan.Span,
                    LayoutDebugNameFormatter.FormatLayoutName(candidate.Layout),
                    candidate.Symbol.Name,
                    candidate.Symbol.StorageClass,
                    rawAddress,
                    candidate.Shape.SizeInAddressUnits);
                return false;
            }
        }

        if (candidate.Symbol.StorageClass == AddressSpace.Hub)
        {
            HubAddress startAddress = fixedAddress.ToHubAddress();
            HubAddress endAddress = startAddress + candidate.Shape.SizeInAddressUnits;
            OccupiedAddressRange overlap = reservedRanges
                .FirstOrDefault(range => RangesOverlap(startAddress, endAddress, range.StartAddress, range.EndAddressExclusive));
            if (overlap != default)
            {
                diagnostics?.ReportInvalidLayoutAddress(
                    candidate.Symbol.SourceSpan.Span,
                    LayoutDebugNameFormatter.FormatLayoutName(candidate.Layout),
                    candidate.Symbol.Name,
                    candidate.Symbol.StorageClass,
                    rawAddress,
                    candidate.Shape.SizeInAddressUnits);
                return false;
            }
        }

        return true;
    }

    private static VirtualAddress? FindFirstFitAddress(
        LayoutCandidate candidate,
        IReadOnlyList<LayoutSlot> occupied,
        IReadOnlyList<OccupiedAddressRange> reservedRanges)
    {
        int address = 0;
        while (true)
        {
            address = AlignUp(address, candidate.AlignmentInAddressUnits);
            int endAddress = checked(address + candidate.Shape.SizeInAddressUnits);
            if (candidate.Symbol.StorageClass == AddressSpace.Lut
                && endAddress > LutAddressSpaceSize)
            {
                return null;
            }

            LayoutSlot? overlappingSlot = occupied
                .OrderBy(static slot => GetRawAddress(slot.Address))
                .FirstOrDefault(slot => RangesOverlap(
                    address,
                    endAddress,
                    GetRawAddress(slot.Address),
                    slot.EndAddressExclusive));

            OccupiedAddressRange overlappingReservedRange = reservedRanges
                .OrderBy(static range => range.StartAddress)
                .FirstOrDefault(range => RangesOverlap(
                    new HubAddress(address),
                    new HubAddress(endAddress),
                    range.StartAddress,
                    range.EndAddressExclusive));

            if (overlappingSlot is null && overlappingReservedRange == default)
                return new VirtualAddress(candidate.Symbol.StorageClass, address);

            if (overlappingSlot is not null
                && (overlappingReservedRange == default
                    || overlappingSlot.EndAddressExclusive <= (int)overlappingReservedRange.EndAddressExclusive))
            {
                address = overlappingSlot.EndAddressExclusive;
                continue;
            }

            address = (int)overlappingReservedRange.EndAddressExclusive;
        }
    }

    private static VirtualAddress? FindHighestFitAddress(
        LayoutCandidate candidate,
        IReadOnlyList<LayoutSlot> occupied)
    {
        int address = 0x1EF - candidate.Shape.SizeInAddressUnits + 1;
        while (address >= 0)
        {
            address = AlignDown(address, candidate.AlignmentInAddressUnits);
            if (address < 0)
                return null;

            int endAddress = checked(address + candidate.Shape.SizeInAddressUnits);
            if (endAddress > FirstSpecialCogAddress)
            {
                address = FirstSpecialCogAddress - candidate.Shape.SizeInAddressUnits;
                continue;
            }

            LayoutSlot? overlappingSlot = occupied
                .OrderByDescending(static slot => GetRawAddress(slot.Address))
                .FirstOrDefault(slot => slot.StorageClass == AddressSpace.Cog
                    && RangesOverlap(
                        address,
                        endAddress,
                        GetRawAddress(slot.Address),
                        slot.EndAddressExclusive));
            if (overlappingSlot is null)
                return new VirtualAddress(AddressSpace.Cog, address);

            address = GetRawAddress(overlappingSlot.Address) - candidate.Shape.SizeInAddressUnits;
        }

        return null;
    }

    private static bool TryAddOccupiedSlot(LayoutSlot slot, ICollection<LayoutSlot> occupied, DiagnosticBag? diagnostics)
    {
        foreach (LayoutSlot existing in occupied)
        {
            if (!RangesOverlap(GetRawAddress(slot.Address), slot.EndAddressExclusive, GetRawAddress(existing.Address), existing.EndAddressExclusive))
                continue;

            diagnostics?.ReportLayoutAddressConflict(
                slot.Symbol.SourceSpan.Span,
                LayoutDebugNameFormatter.FormatLayoutName(slot.Layout),
                slot.Symbol.Name,
                slot.StorageClass,
                GetRawAddress(slot.Address),
                LayoutDebugNameFormatter.FormatLayoutName(existing.Layout),
                existing.Symbol.Name,
                GetRawAddress(existing.Address));
            return false;
        }

        occupied.Add(slot);
        return true;
    }

    private static bool RangesOverlap(HubAddress startA, HubAddress endA, HubAddress startB, HubAddress endB)
    {
        return startA < endB && startB < endA;
    }

    private static bool RangesOverlap(int startA, int endA, int startB, int endB)
    {
        return startA < endB && startB < endA;
    }

    private static int AlignUp(int value, int alignment)
    {
        Requires.NonNegative(value);
        Requires.Positive(alignment);
        int remainder = value % alignment;
        return remainder == 0 ? value : checked(value + (alignment - remainder));
    }

    private static int AlignDown(int value, int alignment)
    {
        Requires.Positive(alignment);
        if (value < 0)
            return value;

        int remainder = value % alignment;
        return remainder == 0 ? value : checked(value - remainder);
    }

    private static int GetRawAddress(VirtualAddress address)
    {
        (_, int rawAddress) = address.GetDataAddress();
        return rawAddress;
    }
}
