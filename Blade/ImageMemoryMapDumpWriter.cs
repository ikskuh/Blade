using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Blade.IR;
using Blade.IR.Mir;
using Blade.Semantics;

namespace Blade;

internal static class ImageMemoryMapDumpWriter
{
    private const int BytesPerLong = 4;
    private const int CogLongCount = 0x200;
    private const int CogUsableLongCount = 0x1F0;
    private const int LutLongCount = 0x200;

    public static string Write(IrBuildResult buildResult)
    {
        Requires.NotNull(buildResult);

        Dictionary<GlobalVariableSymbol, string> initialValues = CollectInitialValues(buildResult.MirModule);
        IReadOnlyList<LayoutSymbol> sharedHubLayouts = CollectSharedHubLayouts(buildResult.ImagePlan);

        StringBuilder sb = new();
        sb.AppendLine("; Image Memory Maps v1");
        sb.AppendLine();

        WriteHubTable(sb, buildResult.LayoutSolution, sharedHubLayouts, initialValues);

        foreach (ImageDescriptor image in buildResult.ImagePlan.Images)
        {
            IReadOnlyList<LayoutSymbol> imageLayouts = CollectLayoutsForImage(image);

            sb.AppendLine();
            sb.Append("image ");
            sb.Append(image.Task.Name);
            if (image.IsEntryImage)
                sb.Append(" entry");
            sb.Append(" mode=");
            sb.AppendLine(image.ExecutionMode.ToString());

            WriteCogTable(sb);
            WriteLutTable(sb, buildResult.LayoutSolution, imageLayouts, initialValues);
        }

        return sb.ToString();
    }

    private static Dictionary<GlobalVariableSymbol, string> CollectInitialValues(MirModule mirModule)
    {
        Dictionary<GlobalVariableSymbol, string> initialValues = [];
        foreach (StorageDefinition definition in mirModule.StorageDefinitions)
        {
            if (definition.InitialValue is null)
                continue;

            GlobalVariableSymbol symbol = definition.Place.Symbol;
            initialValues[symbol] = definition.InitialValue.Format();
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

        return layouts.OrderBy(static layout => layout.Name, StringComparer.Ordinal).ToList();
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

        return layouts.OrderBy(static layout => layout.Name, StringComparer.Ordinal).ToList();
    }

    private static void CollectLayoutTree(LayoutSymbol layout, ISet<LayoutSymbol> seen)
    {
        if (!seen.Add(layout))
            return;

        foreach (LayoutSymbol parent in layout.Parents)
            CollectLayoutTree(parent, seen);
    }

    private static void WriteHubTable(
        StringBuilder sb,
        LayoutSolution layoutSolution,
        IReadOnlyList<LayoutSymbol> layouts,
        IReadOnlyDictionary<GlobalVariableSymbol, string> initialValues)
    {
        sb.AppendLine("shared hub");
        WriteHeader(sb);

        HashSet<LayoutSymbol> selectedLayouts = [.. layouts];
        List<LayoutSlot> slots = layoutSolution.Slots
            .Where(slot => slot.StorageClass == VariableStorageClass.Hub && selectedLayouts.Contains(slot.Layout))
            .OrderBy(static slot => slot.Address)
            .ThenBy(static slot => slot.Layout.Name, StringComparer.Ordinal)
            .ThenBy(static slot => slot.Symbol.Name, StringComparer.Ordinal)
            .ToList();

        Dictionary<int, List<(int StartByteOffset, int EndByteOffsetExclusive, LayoutSlot Slot, string InitialValue)>> rows = [];
        foreach (LayoutSlot slot in slots)
        {
            string initialValue = initialValues.GetValueOrDefault(slot.Symbol, "-");
            int firstLong = slot.Address / BytesPerLong;
            int lastLong = (slot.EndAddressExclusive - 1) / BytesPerLong;
            for (int longIndex = firstLong; longIndex <= lastLong; longIndex++)
            {
                int longStartByte = longIndex * BytesPerLong;
                int startByteOffset = Math.Max(slot.Address, longStartByte) - longStartByte;
                int endByteOffsetExclusive = Math.Min(slot.EndAddressExclusive, longStartByte + BytesPerLong) - longStartByte;
                if (!rows.TryGetValue(longIndex, out List<(int StartByteOffset, int EndByteOffsetExclusive, LayoutSlot Slot, string InitialValue)>? fragments))
                {
                    fragments = [];
                    rows.Add(longIndex, fragments);
                }

                fragments.Add((startByteOffset, endByteOffsetExclusive, slot, initialValue));
            }
        }

        int rowCount = rows.Count == 0 ? 0 : rows.Keys.Max() + 1;
        if (rowCount == 0)
        {
            sb.AppendLine("(none)");
            return;
        }

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            if (!rows.TryGetValue(rowIndex, out List<(int StartByteOffset, int EndByteOffsetExclusive, LayoutSlot Slot, string InitialValue)>? fragments))
            {
                int freeRunStart = rowIndex;
                while ((rowIndex + 1) < rowCount && !rows.ContainsKey(rowIndex + 1))
                    rowIndex++;

                int freeRunLength = rowIndex - freeRunStart + 1;
                if (freeRunLength == 1)
                {
                    WriteRow(sb, freeRunStart, "free", "-", "-");
                    continue;
                }

                sb.AppendLine("*");
                WriteRow(sb, rowIndex, "free", "-", "-");
                continue;
            }

            fragments.Sort(static (left, right) =>
            {
                int cmp = left.StartByteOffset.CompareTo(right.StartByteOffset);
                if (cmp != 0)
                    return cmp;

                cmp = StringComparer.Ordinal.Compare(left.Slot.Layout.Name, right.Slot.Layout.Name);
                return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(left.Slot.Symbol.Name, right.Slot.Symbol.Name);
            });

            bool hasPackedFragments = fragments.Count > 1
                || fragments[0].StartByteOffset != 0
                || fragments[0].EndByteOffsetExclusive != BytesPerLong;
            if (!hasPackedFragments)
            {
                LayoutSlot slot = fragments[0].Slot;
                WriteRow(sb, rowIndex, "allocated", fragments[0].InitialValue, $"{slot.Layout.Name}.{slot.Symbol.Name}");
                continue;
            }

            WriteRow(sb, rowIndex, "allocated", "packed", $"{fragments.Count} occupant(s)");
            foreach ((int startByteOffset, int endByteOffsetExclusive, LayoutSlot slot, string initialValue) in fragments)
                WriteSubRow(sb, DescribeByteRange(startByteOffset, endByteOffsetExclusive), initialValue, $"{slot.Layout.Name}.{slot.Symbol.Name}");
        }
    }

    private static void WriteCogTable(StringBuilder sb)
    {
        sb.AppendLine("cog");
        WriteHeader(sb);
        for (int rowIndex = 0; rowIndex < CogLongCount; rowIndex++)
        {
            if (rowIndex >= CogUsableLongCount)
            {
                WriteRow(sb, rowIndex, "reserved", "-", "-");
                continue;
            }

            WriteRow(sb, rowIndex, "free", "-", "-");
        }
    }

    private static void WriteLutTable(
        StringBuilder sb,
        LayoutSolution layoutSolution,
        IReadOnlyList<LayoutSymbol> layouts,
        IReadOnlyDictionary<GlobalVariableSymbol, string> initialValues)
    {
        sb.AppendLine("lut");
        WriteHeader(sb);

        HashSet<LayoutSymbol> selectedLayouts = [.. layouts];
        Dictionary<int, (LayoutSlot Slot, string InitialValue)> rows = [];
        foreach (LayoutSlot slot in layoutSolution.Slots
                     .Where(slot => slot.StorageClass == VariableStorageClass.Lut && selectedLayouts.Contains(slot.Layout))
                     .OrderBy(static slot => slot.Address)
                     .ThenBy(static slot => slot.Layout.Name, StringComparer.Ordinal)
                     .ThenBy(static slot => slot.Symbol.Name, StringComparer.Ordinal))
        {
            string initialValue = initialValues.GetValueOrDefault(slot.Symbol, "-");
            for (int rowIndex = slot.Address; rowIndex < slot.EndAddressExclusive; rowIndex++)
                rows[rowIndex] = (slot, initialValue);
        }

        for (int rowIndex = 0; rowIndex < LutLongCount; rowIndex++)
        {
            if (!rows.TryGetValue(rowIndex, out (LayoutSlot Slot, string InitialValue) row))
            {
                WriteRow(sb, rowIndex, "free", "-", "-");
                continue;
            }

            WriteRow(sb, rowIndex, "allocated", row.InitialValue, $"{row.Slot.Layout.Name}.{row.Slot.Symbol.Name}");
        }
    }

    private static void WriteHeader(StringBuilder sb)
    {
        sb.AppendLine("addr  state      init   owner");
    }

    private static void WriteRow(StringBuilder sb, int address, string state, string initialValue, string owner)
    {
        sb.Append(FormatAddress(address));
        sb.Append("  ");
        sb.Append(state.PadRight(9, ' '));
        sb.Append("  ");
        sb.Append(initialValue.PadRight(5, ' '));
        sb.Append("  ");
        sb.AppendLine(owner);
    }

    private static void WriteSubRow(StringBuilder sb, string range, string initialValue, string owner)
    {
        sb.Append("      ");
        sb.Append(range.PadRight(9, ' '));
        sb.Append("  ");
        sb.Append(initialValue.PadRight(5, ' '));
        sb.Append("  ");
        sb.AppendLine(owner);
    }

    private static string FormatAddress(int address)
    {
        return $"${address:X3}";
    }

    private static string DescribeByteRange(int startByteOffset, int endByteOffsetExclusive)
    {
        int lastByteOffset = endByteOffsetExclusive - 1;
        if (startByteOffset == lastByteOffset)
            return $"byte[{startByteOffset}]";

        return $"byte[{startByteOffset}..{lastByteOffset}]";
    }
}
