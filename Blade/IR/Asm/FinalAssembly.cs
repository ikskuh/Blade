using System;
using System.IO;
using System.Text;

namespace Blade.IR.Asm;

public sealed class FinalAssembly(string conSectionContents, string datSectionContents, string text)
{
    public string ConSectionContents { get; } = Requires.NotNull(conSectionContents);
    public string DatSectionContents { get; } = Requires.NotNull(datSectionContents);
    public string Text { get; } = Requires.NotNull(text);
}

internal static class FinalAssemblyComposer
{
    public static FinalAssembly Compose(string conSectionContents, string datSectionContents)
    {
        Requires.NotNull(conSectionContents);
        Requires.NotNull(datSectionContents);
        return new FinalAssembly(conSectionContents, datSectionContents, ComposeRaw(conSectionContents, datSectionContents));
    }

    private static string ComposeRaw(string conSectionContents, string datSectionContents)
    {
        StringBuilder builder = new();
        if (conSectionContents.Length > 0)
        {
            builder.AppendLine("CON");
            builder.Append(conSectionContents);
            if (!conSectionContents.EndsWith(Environment.NewLine, StringComparison.Ordinal))
                builder.AppendLine();
            builder.AppendLine();
        }

        builder.AppendLine("DAT");
        builder.AppendLine("    org 0");
        builder.Append(datSectionContents);
        if (!datSectionContents.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            builder.AppendLine();
        return builder.ToString();
    }
}
