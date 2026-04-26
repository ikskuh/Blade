using System;
using Blade.IR.Asm;
using Blade.Semantics;

namespace Blade.IR;

/// <summary>
/// Describes the backend-visible storage footprint of one stored value in one memory space.
/// The shape intentionally matches the current assembler/data-emission model so layout solving,
/// ASM data blocks, and later image packing reason about the same size and alignment facts.
/// </summary>
internal sealed class StorageLayoutShape(
    RuntimeTypeSymbol elementType,
    int entryCount,
    int sizeInAddressUnits,
    int defaultAlignmentInAddressUnits)
{
    /// <summary>
    /// Gets the runtime element type emitted for this storage item.
    /// </summary>
    public RuntimeTypeSymbol ElementType { get; } = Requires.NotNull(elementType);

    /// <summary>
    /// Gets the number of emitted data entries used for this item.
    /// </summary>
    public int EntryCount { get; } = Requires.Positive(entryCount);

    /// <summary>
    /// Gets the total occupied size in storage-space address units.
    /// For hub space this is bytes; for cog/lut space this is long slots.
    /// </summary>
    public int SizeInAddressUnits { get; } = Requires.Positive(sizeInAddressUnits);

    /// <summary>
    /// Gets the default alignment in storage-space address units.
    /// </summary>
    public int DefaultAlignmentInAddressUnits { get; } = Requires.Positive(defaultAlignmentInAddressUnits);

    /// <summary>
    /// Computes the emitted storage footprint for one global variable.
    /// </summary>
    public static StorageLayoutShape FromVariable(GlobalVariableSymbol symbol)
    {
        Requires.NotNull(symbol);
        return FromVariable(symbol, symbol.StorageClass);
    }

    /// <summary>
    /// Computes the emitted storage footprint for one variable in the specified storage space.
    /// </summary>
    public static StorageLayoutShape FromVariable(VariableSymbol symbol, VariableStorageClass storageClass)
    {
        Requires.NotNull(symbol);

        RuntimeTypeSymbol elementType = GetElementType(symbol);
        int elementCount = symbol.Type is ArrayTypeSymbol { Length: int length } ? length : 1;
        int entryCount = storageClass switch
        {
            VariableStorageClass.Cog or VariableStorageClass.Lut => elementCount * elementType.GetSizeInMemorySpace(storageClass),
            VariableStorageClass.Hub when elementType is AggregateTypeSymbol => elementCount * GetAggregateLaneCount(elementType),
            VariableStorageClass.Hub => elementCount,
            _ => Assert.UnreachableValue<int>(), // pragma: force-coverage
        };

        int sizeInAddressUnits = storageClass == VariableStorageClass.Hub
            ? entryCount * GetDirectiveWidthBytes(elementType)
            : entryCount;
        int defaultAlignmentInAddressUnits = elementType.GetAlignmentInMemorySpace(storageClass);
        return new StorageLayoutShape(elementType, entryCount, sizeInAddressUnits, defaultAlignmentInAddressUnits);
    }

    /// <summary>
    /// Computes the emitted storage footprint for one resolved storage place.
    /// </summary>
    public static StorageLayoutShape FromPlace(StoragePlace place)
    {
        Requires.NotNull(place);
        return FromVariable(place.Symbol, place.StorageClass);
    }

    private static RuntimeTypeSymbol GetElementType(VariableSymbol symbol)
    {
        Requires.NotNull(symbol);

        if (symbol.Type is ArrayTypeSymbol arrayType)
            return arrayType.ElementType as RuntimeTypeSymbol
                ?? Assert.UnreachableValue<RuntimeTypeSymbol>(); // pragma: force-coverage

        return symbol.Type as RuntimeTypeSymbol
            ?? Assert.UnreachableValue<RuntimeTypeSymbol>(); // pragma: force-coverage
    }

    private static int GetAggregateLaneCount(RuntimeTypeSymbol type)
    {
        return Math.Max(1, (Requires.NotNull(type).SizeBytes + 3) / 4);
    }

    private static int GetDirectiveWidthBytes(RuntimeTypeSymbol elementType)
    {
        return SelectDirective(elementType) switch
        {
            AsmDataDirective.Byte => 1,
            AsmDataDirective.Word => 2,
            AsmDataDirective.Long => 4,
            _ => Assert.UnreachableValue<int>(), // pragma: force-coverage
        };
    }

    private static AsmDataDirective SelectDirective(RuntimeTypeSymbol type)
    {
        if (type.ScalarWidthBits is int width)
        {
            if (width <= 8)
                return AsmDataDirective.Byte;
            if (width <= 16)
                return AsmDataDirective.Word;
        }

        return type.SizeBytes switch
        {
            <= 1 => AsmDataDirective.Byte,
            <= 2 => AsmDataDirective.Word,
            _ => AsmDataDirective.Long,
        };
    }
}
