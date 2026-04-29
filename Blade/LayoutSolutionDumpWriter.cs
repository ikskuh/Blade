using System.Collections.Generic;
using System.Linq;
using System.Text;
using Blade.IR;

namespace Blade;

internal static class LayoutSolutionDumpWriter
{
    public static string Write(LayoutSolution layoutSolution)
    {
        Requires.NotNull(layoutSolution);

        StringBuilder sb = new();
        sb.AppendLine("; Layout Solution v1");

        IReadOnlyList<IGrouping<Blade.Semantics.LayoutSymbol, LayoutSlot>> layouts = layoutSolution.Slots
            .OrderBy(static slot => (int)slot.StorageClass)
            .ThenBy(static slot => slot.Layout.Name, System.StringComparer.Ordinal)
            .ThenBy(static slot => GetRawAddress(slot.Address))
            .ThenBy(static slot => slot.Symbol.Name, System.StringComparer.Ordinal)
            .GroupBy(static slot => slot.Layout)
            .OrderBy(static group => group.Key.Name, System.StringComparer.Ordinal)
            .ToList();

        foreach (IGrouping<Blade.Semantics.LayoutSymbol, LayoutSlot> layout in layouts)
        {
            sb.Append("layout ");
            sb.AppendLine(layout.Key.Name);
            sb.AppendLine("{");

            foreach (LayoutSlot slot in layout)
            {
                sb.Append("  ");
                sb.Append(slot.StorageClass);
                sb.Append(' ');
                sb.Append(slot.Symbol.Name);
                sb.Append(" @");
                sb.Append(slot.Address);
                sb.Append(" size=");
                sb.Append(slot.SizeInAddressUnits);
                sb.Append(" align=");
                sb.Append(slot.AlignmentInAddressUnits);
                sb.AppendLine();
            }

            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    private static int GetRawAddress(VirtualAddress address)
    {
        (_, int rawAddress) = address.GetDataAddress();
        return rawAddress;
    }
}
