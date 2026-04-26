using System.Collections.Generic;
using Blade;
using Blade.Semantics;

namespace Blade.IR;

public enum StoragePlacePlacement
{
    Allocatable,
    FixedAlias,
    ExternalAlias,
}

public enum StoragePlaceRegisterRole
{
    Global,
    InternalShared,
    InternalDedicated,
}

public sealed class StoragePlace : IAsmSymbol
{
    private string? _emittedName;
    private LayoutSlot? _resolvedLayoutSlot;

    public StoragePlace(
        GlobalVariableSymbol symbol,
        StoragePlacePlacement placement,
        StoragePlaceRegisterRole? registerRole = null,
        string? emittedName = null,
        IReadOnlyList<P2Register>? preferredRegisters = null,
        P2SpecialRegister? specialRegisterAlias = null)
    {
        Symbol = Requires.NotNull(symbol);
        Placement = placement;
        PreferredRegisters = preferredRegisters ?? [];
        SpecialRegisterAlias = specialRegisterAlias;

        bool isAllocatableRegisterPlace = placement == StoragePlacePlacement.Allocatable
            && symbol.StorageClass == VariableStorageClass.Cog;
        Assert.Invariant(isAllocatableRegisterPlace == registerRole.HasValue, "Register role must be present exactly for allocatable register storage places.");
        RegisterRole = registerRole;

        switch (placement)
        {
            case StoragePlacePlacement.Allocatable:
                bool isLayoutAllocatedMember = symbol.DeclaringLayout is not null && !symbol.IsExtern;
                Assert.Invariant(
                    !symbol.FixedAddress.HasValue || isLayoutAllocatedMember,
                    "Allocatable storage places may only carry fixed addresses for layout-solved storage.");
                Assert.Invariant(!symbol.IsExtern, "Allocatable storage places must not be extern.");
                break;

            case StoragePlacePlacement.FixedAlias:
                Assert.Invariant(symbol.FixedAddress.HasValue, "Fixed alias storage places require a fixed address.");
                break;

            case StoragePlacePlacement.ExternalAlias:
                Assert.Invariant(symbol.IsExtern, "External alias storage places must be extern.");
                break;
        }

        if (specialRegisterAlias.HasValue)
        {
            Assert.Invariant(
                placement is StoragePlacePlacement.FixedAlias or StoragePlacePlacement.ExternalAlias,
                "Special-register aliases are only valid for fixed or external aliases.");
            Assert.Invariant(symbol.StorageClass == VariableStorageClass.Cog, "Special-register aliases must live in register space.");
        }

        if (!string.IsNullOrWhiteSpace(emittedName))
            _emittedName = emittedName;
    }

    public GlobalVariableSymbol Symbol { get; }
    public StoragePlacePlacement Placement { get; }
    public StoragePlaceRegisterRole? RegisterRole { get; }
    public IReadOnlyList<P2Register> PreferredRegisters { get; }
    public P2SpecialRegister? SpecialRegisterAlias { get; }
    public int? FixedAddress => Symbol.FixedAddress;
    public VariableStorageClass StorageClass => Symbol.StorageClass;
    internal bool HasAssignedEmittedName => _emittedName is not null;

    public bool IsInternalRegisterSlot => RegisterRole is StoragePlaceRegisterRole.InternalShared
        or StoragePlaceRegisterRole.InternalDedicated;

    public bool IsDedicatedRegisterSlot => RegisterRole is StoragePlaceRegisterRole.Global
        or StoragePlaceRegisterRole.InternalDedicated;

    public bool IsAllocatable => Placement == StoragePlacePlacement.Allocatable;

    public bool IsFixedAlias => Placement == StoragePlacePlacement.FixedAlias;

    public bool IsExternalAlias => Placement == StoragePlacePlacement.ExternalAlias;

    internal bool CanElideTopLevelStoreLoadChains => Symbol.CanElideTopLevelStoreLoadChains;
    internal LayoutSlot? ResolvedLayoutSlot => _resolvedLayoutSlot;

    public bool EmitsStorageLabel => Placement != StoragePlacePlacement.ExternalAlias;

    public string EmittedName => Assert.NotNull(_emittedName, "Storage place emitted names must be assigned by backend naming."); // pragma: force-coverage

    string IAsmSymbol.Name => EmittedName;

    public SymbolType SymbolType => StorageClass switch
    {
        VariableStorageClass.Lut => SymbolType.LutVariable,
        VariableStorageClass.Hub => SymbolType.HubVariable,
        _ => SymbolType.RegVariable,
    };

    internal void AssignEmittedName(string emittedName)
    {
        _emittedName = Requires.NotNullOrWhiteSpace(emittedName);
    }

    internal void AssignResolvedLayoutSlot(LayoutSlot layoutSlot)
    {
        Requires.NotNull(layoutSlot);
        Assert.Invariant(
            _resolvedLayoutSlot is null,
            "Resolved layout slots must only be assigned once.");
        Assert.Invariant(
            ReferenceEquals(layoutSlot.Symbol, Symbol),
            "Resolved layout slots must be assigned to the matching storage symbol.");
        Assert.Invariant(
            Placement == StoragePlacePlacement.Allocatable,
            "Only allocatable storage places can receive solved layout slots.");
        _resolvedLayoutSlot = layoutSlot;
    }
}
