using System;
using System.Collections.Generic;

namespace Blade.IR.Mir;

public static class MirOptimizer
{
    public static MirModule Optimize(
        MirModule module,
        int maxIterations,
        IReadOnlyList<string> enabledOptimizations)
    {
        Requires.NotNull(module);
        Requires.NotNull(enabledOptimizations);

        MirModule current = module;
        int iterations = Math.Max(1, maxIterations);
        for (int i = 0; i < iterations; i++)
        {
            bool changed = false;
            foreach (string name in enabledOptimizations)
            {
                if (OptimizationRegistry.IsMirRunAfterIterations(name))
                    continue;

                IMirOptimization optimization = OptimizationRegistry.GetMirOptimization(name);
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
        foreach (string name in enabledOptimizations)
        {
            if (!OptimizationRegistry.IsMirRunAfterIterations(name))
                continue;

            IMirOptimization optimization = OptimizationRegistry.GetMirOptimization(name);
            MirModule? result = optimization.Run(current);
            if (result is not null)
                current = result;
        }

        return current;
    }
}
