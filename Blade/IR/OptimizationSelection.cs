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
    public static readonly IReadOnlyList<string> MirDefaultOrder =
    [
        "single-callsite-inline",
        "cost-inline",
        "const-prop",
        "copy-prop",
        "cfg-simplify",
        "dce",
    ];

    public static readonly IReadOnlyList<string> LirDefaultOrder =
    [
        "copy-prop",
        "cfg-simplify",
        "dce",
    ];

    public static readonly IReadOnlyList<string> AsmirDefaultOrder =
    [
        "copy-prop",
        "dce-reg",
        "drop-jmp-next",
        "ret-fusion",
        "conditional-move-fusion",
        "muxc-fusion",
        "elide-nops",
        "cleanup-self-mov",
    ];

    public static bool IsKnown(OptimizationStage stage, string name)
        => GetDefaultOrder(stage).Contains(name, StringComparer.Ordinal);

    public static IReadOnlyList<string> GetDefaultOrder(OptimizationStage stage)
        => stage switch
        {
            OptimizationStage.Mir => MirDefaultOrder,
            OptimizationStage.Lir => LirDefaultOrder,
            OptimizationStage.Asmir => AsmirDefaultOrder,
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
        };

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
