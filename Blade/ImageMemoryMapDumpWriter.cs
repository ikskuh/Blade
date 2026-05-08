using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Blade.Reports;

using static Blade.Reports.BasicTextSpanKind;
using static Blade.Reports.SemanticTextSpanKind;

namespace Blade;

internal static class ImageMemoryMapDumpWriter
{
    public static void Write(ITextReportBuilder builder, Blade.IR.CompilationStageOutput buildResult)
    {
        Requires.NotNull(builder);
        Requires.NotNull(buildResult);

        ImageMemoryMapModel model = ImageMemoryMapModelBuilder.Build(buildResult);
        Writer writer = new(builder);
        writer.WriteModel(model);
    }

    public static string Write(Blade.IR.CompilationStageOutput buildResult)
    {
        Requires.NotNull(buildResult);

        StringBuilder builder = new();
        Write(new PlainTextReportBuilder(builder), buildResult);
        return builder.ToString();
    }

    private sealed class Writer(ITextReportBuilder builder) : TextReportBuilderBase(builder)
    {
        public void WriteModel(ImageMemoryMapModel model)
        {
            AppendLine((Comment, "; Image Memory Maps v1"));
            NewLine();

            WriteHubTable(model.SharedHubRows);

            foreach (ImageMemoryMapImage image in model.Images)
            {
                NewLine();
                Append((Keyword, "image"), ' ', (TaskName, image.Placement.Image.Task, image.Placement.Image.Task.Name));
                if (image.Placement.Image.IsEntryImage)
                    Append(' ', (Keyword, "entry"));
                Append(' ', (Keyword, "mode"), '=', (Literal, image.Placement.Image.ExecutionMode.ToString()));
                NewLine();

                WriteTable("cog", image.CogRows);
                WriteTable("lut", image.LutRows);
            }
        }

        private void WriteHubTable(IReadOnlyList<SharedHubRow> rows)
        {
            AppendLine((Keyword, "shared"), ' ', (Keyword, "hub"));
            AppendLine((Literal, "addr"), Space(4), (Literal, "value"), Space(8), (Keyword, "allocated"));

            if (rows.Count == 0)
            {
                AppendLine((Literal, "(none)"));
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
                    WriteHubRow(first);
                    index = runEndExclusive;
                    continue;
                }

                WriteHubRow(first);
                if (runLength > 2)
                    AppendLine((Literal, "*"));
                WriteHubRow(rows[runEndExclusive - 1]);
                index = runEndExclusive;
            }
        }

        private void WriteTable(string title, IReadOnlyList<MemoryMapRow> rows)
        {
            AppendLine((Keyword, title));
            AppendLine((Literal, "addr"), Space(2), (Keyword, "state"), Space(6), (Keyword, "init"), Space(3), (Keyword, "owner"));

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
                    WriteRow(first.Address, first.State, first.InitialValue, first.Owner);
                    index = runEndExclusive;
                    continue;
                }

                WriteRow(first.Address, first.State, first.InitialValue, first.Owner);
                if (runLength > 2)
                    AppendLine((Literal, "*"));
                MemoryMapRow last = rows[runEndExclusive - 1];
                WriteRow(last.Address, last.State, last.InitialValue, last.Owner);
                index = runEndExclusive;
            }
        }

        private void WriteHubRow(SharedHubRow row)
        {
            Append((Literal, FormatHubAddress(row.Address)), Space(2), (Literal, row.Byte0), ' ', (Literal, row.Byte1), ' ', (Literal, row.Byte2), ' ', (Literal, row.Byte3), Space(2), (Comment, row.Owner));
            NewLine();
        }

        private void WriteRow(int address, MemoryMapState state, string initialValue, string owner)
        {
            string stateText = FormatState(state);
            Append(
                (Literal, FormatAddress(address)),
                Space(2),
                (Literal, stateText),
                Space(System.Math.Max(0, 9 - stateText.Length)),
                Space(2),
                (Literal, initialValue),
                Space(System.Math.Max(0, 5 - initialValue.Length)),
                Space(2),
                (Comment, owner));
            NewLine();
        }
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
