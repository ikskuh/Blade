using System;
using Blade.Semantics;

namespace Blade.IR;

public enum StoragePlaceKind
{
    AllocatableGlobalRegister,
    FixedRegisterAlias,
    ExternalAlias,
}

public sealed class StoragePlace
{
    public StoragePlace(
        VariableSymbol symbol,
        StoragePlaceKind kind,
        int? fixedAddress,
        object? staticInitializer)
    {
        Symbol = symbol;
        Kind = kind;
        FixedAddress = fixedAddress;
        StaticInitializer = staticInitializer;
    }

    public VariableSymbol Symbol { get; }
    public StoragePlaceKind Kind { get; }
    public int? FixedAddress { get; }
    public object? StaticInitializer { get; }

    public bool HasStaticInitializer => StaticInitializer is not null;

    public string EmittedName => Kind switch
    {
        StoragePlaceKind.FixedRegisterAlias => Symbol.Name,
        StoragePlaceKind.ExternalAlias => Symbol.Name,
        _ => $"g_{Sanitize(Symbol.Name)}_{Symbol.Id}",
    };

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
