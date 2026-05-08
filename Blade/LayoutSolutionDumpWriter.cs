using System.Collections.Generic;
using System.Linq;
using System.Text;
using Blade.IR;
using Blade.Reports;

namespace Blade;

internal static class LayoutSolutionDumpWriter
{
    public static void Write(ITextReportBuilder writer, LayoutSolution layoutSolution)
    {
        Requires.NotNull(writer);
        Requires.NotNull(layoutSolution);

        writer.Append(BasicTextSpanKind.Comment, "; Layout Solution v1");
        writer.NewLine();

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
            writer.Append(BasicTextSpanKind.Keyword, "layout");
            writer.Append(BasicTextSpanKind.Whitespace, " ");
            writer.Append(SemanticTextSpanKind.LayoutName, layout.Key, LayoutDebugNameFormatter.FormatLayoutName(layout.Key));
            writer.NewLine();
            writer.Append(BasicTextSpanKind.Punctuation, "{");
            writer.NewLine();

            foreach (LayoutSlot slot in layout)
            {
                writer.Append(BasicTextSpanKind.Whitespace, "  ");
                writer.Append(BasicTextSpanKind.Literal, slot.StorageClass.ToString());
                writer.Append(BasicTextSpanKind.Whitespace, " ");
                writer.Append(SemanticTextSpanKind.VariableName, slot.Symbol, slot.Symbol.Name);
                writer.Append(BasicTextSpanKind.Whitespace, " ");
                writer.Append(BasicTextSpanKind.Punctuation, "@");
                writer.Append(BasicTextSpanKind.Literal, slot.Address.ToString());
                writer.Append(BasicTextSpanKind.Whitespace, " ");
                writer.Append(BasicTextSpanKind.Keyword, "size");
                writer.Append(BasicTextSpanKind.Punctuation, "=");
                writer.Append(BasicTextSpanKind.Literal, slot.SizeInAddressUnits.ToString(System.Globalization.CultureInfo.InvariantCulture));
                writer.Append(BasicTextSpanKind.Whitespace, " ");
                writer.Append(BasicTextSpanKind.Keyword, "align");
                writer.Append(BasicTextSpanKind.Punctuation, "=");
                writer.Append(BasicTextSpanKind.Literal, slot.AlignmentInAddressUnits.ToString(System.Globalization.CultureInfo.InvariantCulture));
                writer.NewLine();
            }

            writer.Append(BasicTextSpanKind.Punctuation, "}");
            writer.NewLine();
        }
    }

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
            sb.AppendLine(LayoutDebugNameFormatter.FormatLayoutName(layout.Key));
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
