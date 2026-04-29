using System.Collections.Generic;
using System.Linq;
using Blade.IR;
using Blade.IR.Asm;
using Blade.IR.Mir;
using Blade.Semantics;

namespace Blade;

internal enum MemoryMapState
{
    Free,
    Allocated,
    Reserved,
}

internal enum HubByteKind
{
    Unallocated,
    AllocatedUnknown,
    ImageUnknown,
    Known,
}

internal sealed class SharedHubRow(int address, string byte0, string byte1, string byte2, string byte3, string owner)
{
    public int Address { get; } = Requires.NonNegative(address);
    public string Byte0 { get; } = Requires.NotNull(byte0);
    public string Byte1 { get; } = Requires.NotNull(byte1);
    public string Byte2 { get; } = Requires.NotNull(byte2);
    public string Byte3 { get; } = Requires.NotNull(byte3);
    public string Owner { get; } = Requires.NotNull(owner);
}

internal sealed class MemoryMapRow(int address, MemoryMapState state, string initialValue, string owner)
{
    public int Address { get; } = Requires.NonNegative(address);
    public MemoryMapState State { get; } = state;
    public string InitialValue { get; } = Requires.NotNull(initialValue);
    public string Owner { get; } = Requires.NotNull(owner);
}

internal sealed class ImageMemoryMapImage(ImagePlacementEntry placement, IReadOnlyList<MemoryMapRow> cogRows, IReadOnlyList<MemoryMapRow> lutRows)
{
    public ImagePlacementEntry Placement { get; } = Requires.NotNull(placement);
    public IReadOnlyList<MemoryMapRow> CogRows { get; } = Requires.NotNull(cogRows);
    public IReadOnlyList<MemoryMapRow> LutRows { get; } = Requires.NotNull(lutRows);
}

internal sealed class ImageMemoryMapModel(IReadOnlyList<SharedHubRow> sharedHubRows, IReadOnlyList<ImageMemoryMapImage> images)
{
    public IReadOnlyList<SharedHubRow> SharedHubRows { get; } = Requires.NotNull(sharedHubRows);
    public IReadOnlyList<ImageMemoryMapImage> Images { get; } = Requires.NotNull(images);
}

internal static class ImageMemoryMapModelBuilder
{
    private const int CogLongCount = 0x200;
    private const int CogUsableLongCount = 0x1F0;
    private const int HubBytesPerRow = 4;
    private const int HubDisplayEndAddress = 0x80000;
    private const int LutLongCount = 0x200;

    private readonly record struct HubByteCell(HubByteKind Kind, byte Value, string Owner);

    public static ImageMemoryMapModel Build(IrBuildResult buildResult)
    {
        Requires.NotNull(buildResult);

        Dictionary<GlobalVariableSymbol, RuntimeBladeValue> initialValues = CollectInitialValues(buildResult.MirModule);
        IReadOnlyList<LayoutSymbol> sharedHubLayouts = CollectSharedHubLayouts(buildResult.ImagePlan);

        IReadOnlyList<SharedHubRow> sharedHubRows = BuildSharedHubRows(
            buildResult.ImagePlacement,
            buildResult.LayoutSolution,
            sharedHubLayouts,
            initialValues);

        List<ImageMemoryMapImage> images = [];
        foreach (ImagePlacementEntry placement in buildResult.ImagePlacement.Images)
        {
            IReadOnlyList<LayoutSymbol> imageLayouts = CollectLayoutsForImage(placement.Image);
            CogResourceLayout cogLayout = buildResult.CogResourceLayouts.Images.Single(layout => ReferenceEquals(layout.Image, placement.Image));
            images.Add(new ImageMemoryMapImage(
                placement,
                BuildCogRows(buildResult.AsmModule, buildResult.CogResourceLayouts, cogLayout, initialValues),
                BuildLutRows(buildResult.LayoutSolution, imageLayouts, initialValues)));
        }

        return new ImageMemoryMapModel(sharedHubRows, images);
    }

    private static Dictionary<GlobalVariableSymbol, RuntimeBladeValue> CollectInitialValues(MirModule mirModule)
    {
        Dictionary<GlobalVariableSymbol, RuntimeBladeValue> initialValues = [];
        foreach (StorageDefinition definition in mirModule.StorageDefinitions)
        {
            if (definition.InitialValue is null)
                continue;

            initialValues[definition.Place.Symbol] = definition.InitialValue;
        }

        return initialValues;
    }

    private static IReadOnlyList<LayoutSymbol> CollectSharedHubLayouts(ImagePlan imagePlan)
    {
        HashSet<LayoutSymbol> layouts = [];
        foreach (ImageDescriptor image in imagePlan.Images)
        {
            foreach (LayoutSymbol layout in CollectLayoutsForImage(image))
                layouts.Add(layout);
        }

        return layouts.OrderBy(static layout => layout.Name, System.StringComparer.Ordinal).ToList();
    }

    private static IReadOnlyList<LayoutSymbol> CollectLayoutsForImage(ImageDescriptor image)
    {
        HashSet<LayoutSymbol> layouts = [];
        foreach (FunctionSymbol function in image.Functions)
        {
            if (function.ImplicitLayout is LayoutSymbol implicitLayout)
                CollectLayoutTree(implicitLayout, layouts);

            foreach (LayoutSymbol associatedLayout in function.AssociatedLayouts)
                CollectLayoutTree(associatedLayout, layouts);
        }

        return layouts.OrderBy(static layout => layout.Name, System.StringComparer.Ordinal).ToList();
    }

    private static void CollectLayoutTree(LayoutSymbol layout, ISet<LayoutSymbol> seen)
    {
        if (!seen.Add(layout))
            return;

        foreach (LayoutSymbol parent in layout.Parents)
            CollectLayoutTree(parent, seen);
    }

    private static IReadOnlyList<SharedHubRow> BuildSharedHubRows(
        ImagePlacement imagePlacement,
        LayoutSolution layoutSolution,
        IReadOnlyList<LayoutSymbol> layouts,
        IReadOnlyDictionary<GlobalVariableSymbol, RuntimeBladeValue> initialValues)
    {
        HubByteCell[] cells = new HubByteCell[HubDisplayEndAddress];

        foreach (ImagePlacementEntry placement in imagePlacement.Images.OrderBy(static entry => entry.HubStartAddressBytes))
        {
            int endAddress = System.Math.Min((int)placement.HubEndAddressExclusive, HubDisplayEndAddress);
            for (int address = (int)placement.HubStartAddressBytes; address < endAddress; address++)
            {
                cells[address] = new HubByteCell(HubByteKind.ImageUnknown, 0, $"image {placement.Image.Task.Name}");
            }
        }

        HashSet<LayoutSymbol> selectedLayouts = [.. layouts];
        foreach (LayoutSlot slot in layoutSolution.Slots
                     .Where(slot => slot.StorageClass == AddressSpace.Hub && selectedLayouts.Contains(slot.Layout))
                     .OrderBy(static slot => GetRawAddress(slot.Address))
                     .ThenBy(static slot => slot.Layout.Name, System.StringComparer.Ordinal)
                     .ThenBy(static slot => slot.Symbol.Name, System.StringComparer.Ordinal))
        {
            string owner = $"{slot.Layout.Name}.{slot.Symbol.Name}";
            int slotAddress = GetRawAddress(slot.Address);
            int endAddress = System.Math.Min(slot.EndAddressExclusive, HubDisplayEndAddress);
            if (initialValues.TryGetValue(slot.Symbol, out RuntimeBladeValue? initialValue)
                && TrySerializeHubValue(initialValue, out byte[] bytes))
            {
                Assert.Invariant(
                    bytes.Length == slot.SizeInAddressUnits,
                    "Serialized hub initializer bytes must match the solved slot size.");
                for (int address = slotAddress; address < endAddress; address++)
                {
                    cells[address] = new HubByteCell(HubByteKind.Known, bytes[address - slotAddress], owner);
                }
            }
            else
            {
                for (int address = slotAddress; address < endAddress; address++)
                {
                    cells[address] = new HubByteCell(HubByteKind.AllocatedUnknown, 0, owner);
                }
            }
        }

        List<SharedHubRow> rows = [];
        for (int address = 0; address < HubDisplayEndAddress; address += HubBytesPerRow)
        {
            rows.Add(new SharedHubRow(
                address,
                FormatHubByte(cells[address + 0]),
                FormatHubByte(cells[address + 1]),
                FormatHubByte(cells[address + 2]),
                FormatHubByte(cells[address + 3]),
                ResolveOwner(cells, address)));
        }

        rows.Add(new SharedHubRow(HubDisplayEndAddress, "--", "--", "--", "--", "-"));
        return rows;
    }

    private static IReadOnlyList<MemoryMapRow> BuildCogRows(
        AsmModule asmModule,
        CogResourceLayoutSet cogResourceLayouts,
        CogResourceLayout imageLayout,
        IReadOnlyDictionary<GlobalVariableSymbol, RuntimeBladeValue> initialValues)
    {
        Dictionary<int, MemoryMapRow> rowsByAddress = [];
        List<MemoryMapRow> rows = [];
        for (int rowIndex = 0; rowIndex < imageLayout.CodeSizeLongs; rowIndex++)
        {
            rowsByAddress[rowIndex] = new MemoryMapRow(rowIndex, MemoryMapState.Allocated, "-", "code");
        }

        foreach (AsmAllocatedStorageDefinition definition in asmModule.DataBlocks
                     .Where(static block => block.Kind is AsmDataBlockKind.Register or AsmDataBlockKind.Constant)
                     .SelectMany(static block => block.Definitions.OfType<AsmAllocatedStorageDefinition>())
                     .OrderBy(static definition => definition.Symbol.Name, System.StringComparer.Ordinal))
        {
            if (!TryGetCogDefinitionAddress(definition.Symbol, cogResourceLayouts, out int address))
                continue;

            bool belongsToImage = definition.Symbol switch
            {
                StoragePlace place => imageLayout.ContainsStableSymbol(place),
                AsmSharedConstantSymbol constant => imageLayout.ContainsStableSymbol(constant),
                AsmSpillSlotSymbol => false,
                _ => false,
            };
            if (!belongsToImage)
                continue;

            string initialValue = definition.Symbol is StoragePlace storagePlace
                && initialValues.TryGetValue(storagePlace.Symbol, out RuntimeBladeValue? value)
                    ? value.Format()
                    : definition.InitialValues?.Count > 0
                        ? definition.InitialValues[0].Format()
                        : "-";
            string owner = definition.Symbol switch
            {
                StoragePlace ownerPlace => ownerPlace.Symbol.DeclaringLayout is LayoutSymbol layout
                    ? $"{layout.Name}.{ownerPlace.Symbol.Name}"
                    : ownerPlace.Symbol.Name,
                _ => definition.Symbol.Name,
            };

            for (int rowIndex = address; rowIndex < address + definition.Count; rowIndex++)
            {
                rowsByAddress[rowIndex] = new MemoryMapRow(rowIndex, MemoryMapState.Allocated, initialValue, owner);
            }
        }

        for (int rowIndex = 0; rowIndex < CogLongCount; rowIndex++)
        {
            if (!rowsByAddress.TryGetValue(rowIndex, out MemoryMapRow? row))
            {
                MemoryMapState state = rowIndex >= CogUsableLongCount ? MemoryMapState.Reserved : MemoryMapState.Free;
                row = new MemoryMapRow(rowIndex, state, "-", "-");
            }

            rows.Add(row);
        }

        return rows;
    }

    private static bool TryGetCogDefinitionAddress(IAsmSymbol symbol, CogResourceLayoutSet cogResourceLayouts, out int address)
    {
        Requires.NotNull(symbol);
        Requires.NotNull(cogResourceLayouts);

        if (symbol is StoragePlace { ResolvedLayoutSlot: LayoutSlot { StorageClass: AddressSpace.Cog } slot })
        {
            address = GetRawAddress(slot.Address);
            return true;
        }

        if (symbol is AsmSpillSlotSymbol spillSlot)
        {
            address = (int)spillSlot.Slot;
            return true;
        }

        if (cogResourceLayouts.TryGetAddress(symbol, out MemoryAddress memoryAddress))
        {
            address = GetRawAddress(memoryAddress.Virtual);
            return true;
        }

        address = 0;
        return false;
    }

    private static IReadOnlyList<MemoryMapRow> BuildLutRows(
        LayoutSolution layoutSolution,
        IReadOnlyList<LayoutSymbol> layouts,
        IReadOnlyDictionary<GlobalVariableSymbol, RuntimeBladeValue> initialValues)
    {
        HashSet<LayoutSymbol> selectedLayouts = [.. layouts];
        Dictionary<int, MemoryMapRow> rowsByAddress = [];
        foreach (LayoutSlot slot in layoutSolution.Slots
                     .Where(slot => slot.StorageClass == AddressSpace.Lut && selectedLayouts.Contains(slot.Layout))
                     .OrderBy(static slot => GetRawAddress(slot.Address))
                     .ThenBy(static slot => slot.Layout.Name, System.StringComparer.Ordinal)
                     .ThenBy(static slot => slot.Symbol.Name, System.StringComparer.Ordinal))
        {
            string initialValue = initialValues.TryGetValue(slot.Symbol, out RuntimeBladeValue? value)
                ? value.Format()
                : "-";
            int startAddress = GetRawAddress(slot.Address);
            int endAddress = slot.EndAddressExclusive;
            for (int rowIndex = startAddress; rowIndex < endAddress; rowIndex++)
            {
                rowsByAddress[rowIndex] = new MemoryMapRow(
                    rowIndex,
                    MemoryMapState.Allocated,
                    initialValue,
                    $"{slot.Layout.Name}.{slot.Symbol.Name}");
            }
        }

        List<MemoryMapRow> rows = [];
        for (int rowIndex = 0; rowIndex < LutLongCount; rowIndex++)
        {
            if (!rowsByAddress.TryGetValue(rowIndex, out MemoryMapRow? row))
                row = new MemoryMapRow(rowIndex, MemoryMapState.Free, "-", "-");
            rows.Add(row);
        }

        return rows;
    }

    private static string ResolveOwner(IReadOnlyList<HubByteCell> cells, int startAddress)
    {
        string? owner = null;
        for (int offset = 0; offset < HubBytesPerRow; offset++)
        {
            string byteOwner = cells[startAddress + offset].Owner;
            if (string.IsNullOrEmpty(byteOwner))
                continue;

            if (owner is null)
            {
                owner = byteOwner;
                continue;
            }

            if (!string.Equals(owner, byteOwner, System.StringComparison.Ordinal))
                return "packed";
        }

        return owner ?? "-";
    }

    private static string FormatHubByte(HubByteCell cell)
    {
        return cell.Kind switch
        {
            HubByteKind.Unallocated => "--",
            HubByteKind.AllocatedUnknown => "--",
            HubByteKind.ImageUnknown => "??",
            HubByteKind.Known => cell.Value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture),
            _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
        };
    }

    private static int GetRawAddress(VirtualAddress address)
    {
        (_, int rawAddress) = address.GetDataAddress();
        return rawAddress;
    }

    private static bool TrySerializeHubValue(RuntimeBladeValue value, out byte[] bytes)
    {
        Requires.NotNull(value);

        if (value.Type is ArrayTypeSymbol && value.Value is IReadOnlyList<RuntimeBladeValue> elements)
        {
            List<byte> flattened = [];
            foreach (RuntimeBladeValue element in elements)
            {
                if (!TrySerializeHubValue(element, out byte[] elementBytes))
                {
                    bytes = [];
                    return false;
                }

                flattened.AddRange(elementBytes);
            }

            bytes = [.. flattened];
            return true;
        }

        if (value.TryGetBool(out bool boolean))
        {
            bytes = [boolean ? (byte)1 : (byte)0];
            return true;
        }

        if (value.TryGetInteger(out long integer))
        {
            int sizeBytes = value.Type.SizeBytes;
            ulong raw = unchecked((ulong)integer);
            bytes = new byte[sizeBytes];
            for (int index = 0; index < sizeBytes; index++)
                bytes[index] = (byte)(raw >> (index * 8));
            return true;
        }

        bytes = [];
        return false;
    }
}
