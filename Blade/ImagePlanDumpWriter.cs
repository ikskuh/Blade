using System.Text;
using Blade.IR;
using Blade.Semantics;

namespace Blade;

internal static class ImagePlanDumpWriter
{
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
