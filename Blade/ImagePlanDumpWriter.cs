using System.Text;
using Blade.IR;
using Blade.Reports;
using Blade.Semantics;

namespace Blade;

internal static class ImagePlanDumpWriter
{
    public static void Write(ITextReportBuilder writer, ImagePlan imagePlan)
    {
        Requires.NotNull(writer);
        Requires.NotNull(imagePlan);

        writer.Append(BasicTextSpanKind.Comment, "; Images v1");
        writer.NewLine();
        foreach (ImageDescriptor image in imagePlan.Images)
        {
            writer.Append(BasicTextSpanKind.Keyword, "image");
            writer.Append(BasicTextSpanKind.Whitespace, " ");
            writer.Append(SemanticTextSpanKind.TaskName, image.Task, image.Task.Name);
            if (image.IsEntryImage)
            {
                writer.Append(BasicTextSpanKind.Whitespace, " ");
                writer.Append(BasicTextSpanKind.Keyword, "entry");
            }

            writer.Append(BasicTextSpanKind.Whitespace, " ");
            writer.Append(BasicTextSpanKind.Keyword, "mode");
            writer.Append(BasicTextSpanKind.Punctuation, "=");
            writer.Append(BasicTextSpanKind.Literal, image.ExecutionMode.ToString());
            writer.NewLine();

            writer.Append(BasicTextSpanKind.Punctuation, "{");
            writer.NewLine();
            writer.Append(BasicTextSpanKind.Whitespace, "  ");
            writer.Append(BasicTextSpanKind.Keyword, "functions");
            writer.NewLine();
            foreach (FunctionSymbol function in image.Functions)
            {
                writer.Append(BasicTextSpanKind.Whitespace, "    ");
                writer.Append(SemanticTextSpanKind.FunctionName, function, function.Name);
                writer.NewLine();
            }

            writer.Append(BasicTextSpanKind.Whitespace, "  ");
            writer.Append(BasicTextSpanKind.Keyword, "storage");
            writer.NewLine();
            foreach (GlobalVariableSymbol storage in image.Storage)
            {
                writer.Append(BasicTextSpanKind.Whitespace, "    ");
                writer.Append(BasicTextSpanKind.Literal, storage.StorageClass.ToString());
                writer.Append(BasicTextSpanKind.Whitespace, " ");
                writer.Append(SemanticTextSpanKind.VariableName, storage, storage.Name);
                writer.NewLine();
            }

            writer.Append(BasicTextSpanKind.Punctuation, "}");
            writer.NewLine();
        }
    }

    public static string Write(ImagePlan imagePlan)
    {
        Requires.NotNull(imagePlan);

        StringBuilder sb = new();
        sb.AppendLine("; Images v1");
        foreach (ImageDescriptor image in imagePlan.Images)
        {
            sb.Append("image ");
            sb.Append(image.Task.Name);
            if (image.IsEntryImage)
                sb.Append(" entry");
            sb.Append(" mode=");
            sb.AppendLine(image.ExecutionMode.ToString());

            sb.AppendLine("{");
            sb.AppendLine("  functions");
            foreach (FunctionSymbol function in image.Functions)
            {
                sb.Append("    ");
                sb.AppendLine(function.Name);
            }

            sb.AppendLine("  storage");
            foreach (GlobalVariableSymbol storage in image.Storage)
            {
                sb.Append("    ");
                sb.Append(storage.StorageClass);
                sb.Append(' ');
                sb.AppendLine(storage.Name);
            }

            sb.AppendLine("}");
        }

        return sb.ToString();
    }
}
