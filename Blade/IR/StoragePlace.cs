using System;
using System.Collections.Generic;
using Blade;
using Blade.Semantics;

namespace Blade.IR;

public enum StoragePlaceKind
{
    AllocatableGlobalRegister,
    AllocatableInternalSharedRegister,
    AllocatableInternalDedicatedRegister,
    FixedRegisterAlias,
    ExternalAlias,
    AllocatableLutEntry,
    FixedLutAlias,
    ExternalLutAlias,
    AllocatableHubEntry,
    FixedHubAlias,
    ExternalHubAlias,
}

public sealed class StoragePlace : IAsmSymbol
{
    private string? _emittedName;

    public StoragePlace(
        Symbol symbol,
        StoragePlaceKind kind,
        int? fixedAddress,
        string? emittedName = null,
        IReadOnlyList<P2Register>? preferredRegisters = null)
    {
        Symbol = Requires.NotNull(symbol);
        Kind = kind;
        FixedAddress = fixedAddress;
        PreferredRegisters = preferredRegisters ?? [];
        if (!string.IsNullOrWhiteSpace(emittedName))
            _emittedName = emittedName;
        if (P2InstructionMetadata.TryParseSpecialRegister(Symbol.Name, out P2SpecialRegister specialRegister))
            SpecialRegisterAlias = new P2Register(specialRegister);
    }

    public Symbol Symbol { get; }
    public StoragePlaceKind Kind { get; }
    public int? FixedAddress { get; }
    public IReadOnlyList<P2Register> PreferredRegisters { get; }
    public P2Register? SpecialRegisterAlias { get; }
    internal bool HasAssignedEmittedName => _emittedName is not null;

    public VariableStorageClass StorageClass => Kind switch
    {
        StoragePlaceKind.AllocatableLutEntry
            or StoragePlaceKind.FixedLutAlias
            or StoragePlaceKind.ExternalLutAlias => VariableStorageClass.Lut,
        StoragePlaceKind.AllocatableHubEntry
            or StoragePlaceKind.FixedHubAlias
            or StoragePlaceKind.ExternalHubAlias => VariableStorageClass.Hub,
        _ => VariableStorageClass.Reg,
    };

    public bool IsInternalRegisterSlot => Kind is StoragePlaceKind.AllocatableInternalSharedRegister
        or StoragePlaceKind.AllocatableInternalDedicatedRegister;

    public bool IsDedicatedRegisterSlot => Kind is StoragePlaceKind.AllocatableGlobalRegister
        or StoragePlaceKind.AllocatableInternalDedicatedRegister;

    internal bool CanElideTopLevelStoreLoadChains => Symbol is VariableSymbol { CanElideTopLevelStoreLoadChains: true };

    public bool EmitsStorageLabel => Kind is StoragePlaceKind.AllocatableGlobalRegister
        or StoragePlaceKind.AllocatableLutEntry
        or StoragePlaceKind.AllocatableHubEntry
        or StoragePlaceKind.FixedRegisterAlias
        or StoragePlaceKind.FixedLutAlias
        or StoragePlaceKind.FixedHubAlias;

    public string EmittedName => Assert.NotNull(_emittedName, "Storage place emitted names must be assigned by backend naming."); // pragma: force-coverage

    string IAsmSymbol.Name => EmittedName;

    public SymbolType SymbolType => Symbol switch
    {
        ParameterSymbol => SymbolType.Parameter,
        VariableSymbol { StorageClass: VariableStorageClass.Lut } => SymbolType.LutVariable,
        VariableSymbol { StorageClass: VariableStorageClass.Hub } => SymbolType.HubVariable,
        _ => SymbolType.RegVariable,
    };

    internal void AssignEmittedName(string emittedName)
    {
        _emittedName = Requires.NotNullOrWhiteSpace(emittedName);
    }
}
