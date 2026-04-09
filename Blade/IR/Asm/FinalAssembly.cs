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
    public static FinalAssembly Compose(string conSectionContents, string datSectionContents, RuntimeTemplate? runtimeTemplate)
    {
        Requires.NotNull(conSectionContents);
        Requires.NotNull(datSectionContents);

        string text = runtimeTemplate is null
            ? ComposeRaw(conSectionContents, datSectionContents)
            : ComposeTemplate(runtimeTemplate, conSectionContents, datSectionContents);

        return new FinalAssembly(conSectionContents, datSectionContents, text);
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

    private static string ComposeTemplate(RuntimeTemplate runtimeTemplate, string conSectionContents, string datSectionContents)
    {
        Requires.NotNull(runtimeTemplate);

        StringBuilder builder = new();
        using StringReader reader = new(runtimeTemplate.TemplateText);
        while (reader.ReadLine() is string line)
        {
            if (RuntimeTemplate.IsConMarkerLine(line))
            {
                AppendSectionContents(builder, conSectionContents);
                continue;
            }

            if (RuntimeTemplate.IsDatMarkerLine(line))
            {
                AppendSectionContents(builder, datSectionContents);
                continue;
            }

            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    private static void AppendSectionContents(StringBuilder builder, string contents)
    {
        Requires.NotNull(builder);
        Requires.NotNull(contents);

        if (contents.Length == 0)
            return;

        builder.Append(contents);
        if (!contents.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            builder.AppendLine();
    }
}
