using System.Collections.Generic;
using System.Linq;
using Blade.Diagnostics;
using Blade.Semantics;
using Blade.Semantics.Bound;

namespace Blade.IR;

/// <summary>
/// Represents the program-wide solved addresses for layout-backed storage.
/// A layout solution is shared across every image so that a given layout member keeps the
/// same physical address whenever that layout is imported. The current implementation solves
/// only hub and LUT layout members; cog/register-space solving will be added later.
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
    int address,
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
    public VariableStorageClass StorageClass { get; } = symbol.StorageClass;

    /// <summary>
    /// Gets the resolved start address in storage-space units.
    /// Hub addresses are bytes; LUT addresses are long slots.
    /// </summary>
    public int Address { get; } = Requires.NonNegative(address);

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
    public int EndAddressExclusive => checked(Address + SizeInAddressUnits);
}

/// <summary>
/// Solves stable storage addresses for layout-backed variables.
/// </summary>
public static class LayoutSolver
{
    private const int LutAddressSpaceSize = 0x200;

    private readonly record struct LayoutCandidate(
        GlobalVariableSymbol Symbol,
        LayoutSymbol Layout,
        StorageLayoutShape Shape,
        int AlignmentInAddressUnits);

    /// <summary>
    /// Solves the program's current layout-backed hub and LUT storage.
    /// </summary>
    public static LayoutSolution Solve(BoundProgram program, DiagnosticBag? diagnostics = null)
    {
        Requires.NotNull(program);

        IReadOnlyList<LayoutSymbol> layouts = CollectLayouts(program);
        List<LayoutSlot> slots = [];
        slots.AddRange(SolveStorageClass(layouts, VariableStorageClass.Lut, diagnostics));
        slots.AddRange(SolveStorageClass(layouts, VariableStorageClass.Hub, diagnostics));
        return new LayoutSolution(slots);
    }

    private static IReadOnlyList<LayoutSlot> SolveStorageClass(
        IReadOnlyList<LayoutSymbol> layouts,
        VariableStorageClass storageClass,
        DiagnosticBag? diagnostics)
    {
        List<LayoutCandidate> candidates = CollectCandidates(layouts, storageClass);
        if (candidates.Count == 0)
            return [];

        List<LayoutSlot> slots = [];
        List<LayoutSlot> occupied = [];
        foreach (LayoutCandidate candidate in candidates.Where(static candidate => candidate.Symbol.FixedAddress.HasValue))
        {
            int fixedAddress = candidate.Symbol.FixedAddress
                ?? Assert.UnreachableValue<int>(); // pragma: force-coverage

            if (!TryValidateAlignment(candidate, diagnostics))
                continue;

            if (!TryValidateFixedAddress(candidate, fixedAddress, diagnostics))
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

        foreach (LayoutCandidate candidate in candidates.Where(static candidate => !candidate.Symbol.FixedAddress.HasValue))
        {
            if (!TryValidateAlignment(candidate, diagnostics))
                continue;

            int? address = FindFirstFitAddress(candidate, occupied);
            if (!address.HasValue)
            {
                diagnostics?.ReportLayoutAllocationFailed(
                    candidate.Symbol.SourceSpan.Span,
                    candidate.Layout.Name,
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

    private static List<LayoutCandidate> CollectCandidates(
        IReadOnlyList<LayoutSymbol> layouts,
        VariableStorageClass storageClass)
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

    private static IReadOnlyList<LayoutSymbol> CollectLayouts(BoundProgram program)
    {
        HashSet<LayoutSymbol> layouts = [];

        foreach (BoundModule module in program.Modules)
        {
            foreach (LayoutSymbol layout in module.ExportedSymbols.Values.OfType<LayoutSymbol>())
                CollectLayoutTree(layout, layouts);
        }

        foreach (BoundFunctionMember function in program.Functions)
        {
            if (function.Symbol.ImplicitLayout is LayoutSymbol implicitLayout)
                CollectLayoutTree(implicitLayout, layouts);

            foreach (LayoutSymbol associatedLayout in function.Symbol.AssociatedLayouts)
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
                candidate.Layout.Name,
                candidate.Symbol.Name,
                candidate.AlignmentInAddressUnits));
        }

        return false;
    }

    private static bool TryValidateFixedAddress(LayoutCandidate candidate, int fixedAddress, DiagnosticBag? diagnostics)
    {
        if (fixedAddress < 0)
        {
            diagnostics?.ReportInvalidLayoutAddress(
                candidate.Symbol.SourceSpan.Span,
                candidate.Layout.Name,
                candidate.Symbol.Name,
                candidate.Symbol.StorageClass,
                fixedAddress,
                candidate.Shape.SizeInAddressUnits);
            return false;
        }

        if (fixedAddress % candidate.AlignmentInAddressUnits != 0)
        {
            diagnostics?.ReportInvalidLayoutAddress(
                candidate.Symbol.SourceSpan.Span,
                candidate.Layout.Name,
                candidate.Symbol.Name,
                candidate.Symbol.StorageClass,
                fixedAddress,
                candidate.Shape.SizeInAddressUnits);
            return false;
        }

        if (candidate.Symbol.StorageClass == VariableStorageClass.Lut
            && fixedAddress + candidate.Shape.SizeInAddressUnits > LutAddressSpaceSize)
        {
            diagnostics?.ReportInvalidLayoutAddress(
                candidate.Symbol.SourceSpan.Span,
                candidate.Layout.Name,
                candidate.Symbol.Name,
                candidate.Symbol.StorageClass,
                fixedAddress,
                candidate.Shape.SizeInAddressUnits);
            return false;
        }

        return true;
    }

    private static int? FindFirstFitAddress(LayoutCandidate candidate, IReadOnlyList<LayoutSlot> occupied)
    {
        int address = 0;
        while (true)
        {
            address = AlignUp(address, candidate.AlignmentInAddressUnits);
            int endAddress = checked(address + candidate.Shape.SizeInAddressUnits);
            if (candidate.Symbol.StorageClass == VariableStorageClass.Lut
                && endAddress > LutAddressSpaceSize)
            {
                return null;
            }

            LayoutSlot? overlapping = occupied
                .OrderBy(static slot => slot.Address)
                .FirstOrDefault(slot => RangesOverlap(address, endAddress, slot.Address, slot.EndAddressExclusive));
            if (overlapping is null)
                return address;

            address = overlapping.EndAddressExclusive;
        }
    }

    private static bool TryAddOccupiedSlot(LayoutSlot slot, ICollection<LayoutSlot> occupied, DiagnosticBag? diagnostics)
    {
        foreach (LayoutSlot existing in occupied)
        {
            if (!RangesOverlap(slot.Address, slot.EndAddressExclusive, existing.Address, existing.EndAddressExclusive))
                continue;

            diagnostics?.ReportLayoutAddressConflict(
                slot.Symbol.SourceSpan.Span,
                slot.Layout.Name,
                slot.Symbol.Name,
                slot.StorageClass,
                slot.Address,
                existing.Layout.Name,
                existing.Symbol.Name,
                existing.Address);
            return false;
        }

        occupied.Add(slot);
        return true;
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
}
