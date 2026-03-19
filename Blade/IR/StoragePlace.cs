using System;
using Blade.Semantics;

namespace Blade.IR;

public enum StoragePlaceKind
{
    AllocatableGlobalRegister,
    FixedRegisterAlias,
    ExternalAlias,
    AllocatableLutEntry,
    FixedLutAlias,
    ExternalLutAlias,
    AllocatableHubEntry,
    FixedHubAlias,
    ExternalHubAlias,
}

public sealed class StoragePlace
{
    public StoragePlace(
        Symbol symbol,
        StoragePlaceKind kind,
        int? fixedAddress,
        object? staticInitializer)
    {
        Symbol = symbol;
        Kind = kind;
        FixedAddress = fixedAddress;
        StaticInitializer = staticInitializer;
    }

    public Symbol Symbol { get; }
    public StoragePlaceKind Kind { get; }
    public int? FixedAddress { get; }
    public object? StaticInitializer { get; }

    public bool HasStaticInitializer => StaticInitializer is not null;

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

    public string EmittedName => Kind switch
    {
        StoragePlaceKind.FixedRegisterAlias
            or StoragePlaceKind.FixedLutAlias
            or StoragePlaceKind.FixedHubAlias => Symbol.Name,
        StoragePlaceKind.ExternalAlias
            or StoragePlaceKind.ExternalLutAlias
            or StoragePlaceKind.ExternalHubAlias => Symbol.Name,
        _ => BuildAllocatableName(Symbol),
    };

    private static string BuildAllocatableName(Symbol symbol)
    {
        string sanitizedName = Sanitize(symbol.Name);
        string prefix = symbol is VariableSymbol variable
            ? variable.StorageClass switch
            {
                VariableStorageClass.Lut => "l",
                VariableStorageClass.Hub => "h",
                _ => "g",
            }
            : "g";

        return symbol is VariableSymbol { IsGlobalStorage: true }
            ? $"{prefix}_{sanitizedName}"
            : $"{prefix}_{sanitizedName}_{symbol.Id}";
    }

    private static string Sanitize(string name)
    {
        char[] chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_')
                chars[i] = '_';
        }

        return new string(chars);
    }
}
