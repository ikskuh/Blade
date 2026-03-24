using System;
using System.Collections.Generic;
using System.Linq;

namespace Blade.IR;

public enum OptimizationStage
{
    Mir,
    Lir,
    Asmir,
}

public readonly record struct OptimizationDirective(OptimizationStage Stage, bool Enable, IReadOnlyList<string> Names);

public static class OptimizationCatalog
{
    public static IReadOnlyList<string> MirDefaultOrder => OptimizationRegistry.GetDefaultOrder(OptimizationStage.Mir);

    public static IReadOnlyList<string> LirDefaultOrder => OptimizationRegistry.GetDefaultOrder(OptimizationStage.Lir);

    public static IReadOnlyList<string> AsmirDefaultOrder => OptimizationRegistry.GetDefaultOrder(OptimizationStage.Asmir);

    public static bool IsKnown(OptimizationStage stage, string name)
        => OptimizationRegistry.IsKnown(stage, name);

    public static IReadOnlyList<string> GetDefaultOrder(OptimizationStage stage)
        => OptimizationRegistry.GetDefaultOrder(stage);

    public static IReadOnlyList<string> ResolveEnabled(
        OptimizationStage stage,
        IEnumerable<OptimizationDirective> directives)
    {
        Requires.NotNull(directives);

        IReadOnlyList<string> defaults = GetDefaultOrder(stage);
        HashSet<string> enabled = new(defaults, StringComparer.Ordinal);

        foreach (OptimizationDirective directive in directives)
        {
            if (directive.Stage != stage)
                continue;

            IReadOnlyList<string> names = directive.Names;
            if (names.Count == 1 && names[0] == "*")
            {
                if (directive.Enable)
                {
                    foreach (string opt in defaults)
                        enabled.Add(opt);
                }
                else
                {
                    enabled.Clear();
                }

                continue;
            }

            foreach (string name in names)
            {
                if (directive.Enable)
                    enabled.Add(name);
                else
                    enabled.Remove(name);
            }
        }

        return defaults.Where(enabled.Contains).ToArray();
    }
}
