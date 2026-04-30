using System.IO;
using Blade.Semantics;

namespace Blade;

internal static class LayoutDebugNameFormatter
{
    public static string FormatLayoutName(LayoutSymbol layout)
    {
        Requires.NotNull(layout);

        string scopeName = GetScopeName(layout.SourceSpan.FilePath);
        return $"{scopeName}.{layout.Name}";
    }

    private static string GetScopeName(string filePath)
    {
        Requires.NotNull(filePath);

        return filePath switch
        {
            "<builtin>" => "builtin",
            "<builtin-runtime>" => "builtin",
            _ => Path.GetFileNameWithoutExtension(filePath),
        };
    }
}