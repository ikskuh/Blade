using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Blade;

internal static class ImageMemoryMapDumpWriter
{
    public static string Write(Blade.IR.IrBuildResult buildResult)
    {
        Requires.NotNull(buildResult);

        ImageMemoryMapModel model = ImageMemoryMapModelBuilder.Build(buildResult);

        StringBuilder sb = new();
        sb.AppendLine("; Image Memory Maps v1");
        sb.AppendLine();

        WriteHubTable(sb, model.SharedHubRows);

        foreach (ImageMemoryMapImage image in model.Images)
        {
            sb.AppendLine();
            sb.Append("image ");
            sb.Append(image.Placement.Image.Task.Name);
            if (image.Placement.Image.IsEntryImage)
                sb.Append(" entry");
            sb.Append(" mode=");
            sb.AppendLine(image.Placement.Image.ExecutionMode.ToString());

            WriteTable(sb, "cog", image.CogRows);
            WriteTable(sb, "lut", image.LutRows);
        }

        return sb.ToString();
    }

    private static void WriteHubTable(StringBuilder sb, IReadOnlyList<SharedHubRow> rows)
    {
        sb.AppendLine("shared hub");
        WriteHubHeader(sb);

        if (rows.Count == 0)
        {
            sb.AppendLine("(none)");
            return;
        }

        int index = 0;
        while (index < rows.Count)
        {
            SharedHubRow first = rows[index];
            int runEndExclusive = index + 1;
            while (runEndExclusive < rows.Count && HaveSameSharedHubPayload(first, rows[runEndExclusive]))
                runEndExclusive++;

            int runLength = runEndExclusive - index;
            if (runLength == 1)
            {
                WriteHubRow(sb, first);
                index = runEndExclusive;
                continue;
            }

            WriteHubRow(sb, first);
            if (runLength > 2)
                sb.AppendLine("*");
            WriteHubRow(sb, rows[runEndExclusive - 1]);
            index = runEndExclusive;
        }
    }

    private static void WriteTable(StringBuilder sb, string title, IReadOnlyList<MemoryMapRow> rows)
    {
        sb.AppendLine(title);
        WriteHeader(sb);

        int index = 0;
        while (index < rows.Count)
        {
            MemoryMapRow first = rows[index];
            int runEndExclusive = index + 1;
            while (runEndExclusive < rows.Count && HaveSameRowPayload(first, rows[runEndExclusive]))
                runEndExclusive++;

            int runLength = runEndExclusive - index;
            if (runLength == 1)
            {
                WriteRow(sb, first.Address, first.State, first.InitialValue, first.Owner);
                index = runEndExclusive;
                continue;
            }

            WriteRow(sb, first.Address, first.State, first.InitialValue, first.Owner);
            if (runLength > 2)
                sb.AppendLine("*");
            MemoryMapRow last = rows[runEndExclusive - 1];
            WriteRow(sb, last.Address, last.State, last.InitialValue, last.Owner);
            index = runEndExclusive;
        }
    }

    private static void WriteHubHeader(StringBuilder sb)
    {
        sb.AppendLine("addr    value        allocated");
    }

    private static void WriteHeader(StringBuilder sb)
    {
        sb.AppendLine("addr  state      init   owner");
    }

    private static void WriteHubRow(StringBuilder sb, SharedHubRow row)
    {
        sb.Append(FormatHubAddress(row.Address));
        sb.Append("  ");
        sb.Append(row.Byte0);
        sb.Append(' ');
        sb.Append(row.Byte1);
        sb.Append(' ');
        sb.Append(row.Byte2);
        sb.Append(' ');
        sb.Append(row.Byte3);
        sb.Append("  ");
        sb.AppendLine(row.Owner);
    }

    private static void WriteRow(StringBuilder sb, int address, MemoryMapState state, string initialValue, string owner)
    {
        sb.Append(FormatAddress(address));
        sb.Append("  ");
        sb.Append(FormatState(state).PadRight(9, ' '));
        sb.Append("  ");
        sb.Append(initialValue.PadRight(5, ' '));
        sb.Append("  ");
        sb.AppendLine(owner);
    }

    private static string FormatAddress(int address)
    {
        return $"${address:X3}";
    }

    private static string FormatHubAddress(int address)
    {
        return $"${address:X5}";
    }

    private static string FormatState(MemoryMapState state)
    {
        return state switch
        {
            MemoryMapState.Free => "free",
            MemoryMapState.Allocated => "allocated",
            MemoryMapState.Reserved => "reserved",
            _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
        };
    }

    private static bool HaveSameSharedHubPayload(SharedHubRow left, SharedHubRow right)
    {
        return left.Byte0 == right.Byte0
            && left.Byte1 == right.Byte1
            && left.Byte2 == right.Byte2
            && left.Byte3 == right.Byte3
            && string.Equals(left.Owner, right.Owner, System.StringComparison.Ordinal);
    }

    private static bool HaveSameRowPayload(MemoryMapRow left, MemoryMapRow right)
    {
        return left.State == right.State
            && string.Equals(left.InitialValue, right.InitialValue, System.StringComparison.Ordinal)
            && string.Equals(left.Owner, right.Owner, System.StringComparison.Ordinal);
    }
}
