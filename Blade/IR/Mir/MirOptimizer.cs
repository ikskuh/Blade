using System;
using System.Collections.Generic;

namespace Blade.IR.Mir;

public static class MirOptimizer
{
    public static MirModule Optimize(
        MirModule module,
        int maxIterations,
        IReadOnlyList<MirOptimization> enabledOptimizations)
    {
        Requires.NotNull(module);
        Requires.NotNull(enabledOptimizations);

        MirModule current = module;
        int iterations = Math.Max(1, maxIterations);
        for (int i = 0; i < iterations; i++)
        {
            bool changed = false;
            foreach (MirOptimization optimization in enabledOptimizations)
            {
                if (optimization.RunAfterIterations)
                    continue;

                MirModule? result = optimization.Run(current);
                if (result is not null)
                {
                    current = result;
                    changed = true;
                }
            }

            if (!changed)
                break;
        }

        // Run post-iteration passes (e.g., flag-propagation).
        foreach (MirOptimization optimization in enabledOptimizations)
        {
            if (!optimization.RunAfterIterations)
                continue;

            MirModule? result = optimization.Run(current);
            if (result is not null)
                current = result;
        }

        return current;
    }
}
