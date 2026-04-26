using System;
using System.Collections.Generic;
using System.Linq;
using Blade;
using Blade.Semantics;

namespace Blade.Diagnostics;

public static class Formatter
{
    public static object? Format(object? input)
    {
        if (input is null)
            return null;
        if (input is VariableStorageClass cls)
            return Format(cls);
        if (input is ICollection<LayoutSymbol> items)
            return Format(items);
        return input;
    }

    public static string Format(VariableStorageClass storageClass) => storageClass switch
    {
        VariableStorageClass.Cog => "cog",
        VariableStorageClass.Lut => "lut",
        VariableStorageClass.Hub => "hub",
        _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
    };

    public static string Format(ICollection<LayoutSymbol> layouts)
    {
        Requires.NotNull(layouts);
        if (layouts.Count == 0)
            return "<none>";
        return string.Join(", ", layouts.Select(static layoutSymbol => layoutSymbol.Name).OrderBy(static name => name, StringComparer.Ordinal));
    }
}
