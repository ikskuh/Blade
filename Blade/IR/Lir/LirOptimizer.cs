using System;
using System.Collections.Generic;

namespace Blade.IR.Lir;

public static class LirOptimizer
{
    public static LirModule Optimize(
        LirModule module,
        int maxIterations,
        IReadOnlyList<LirOptimization> enabledOptimizations)
    {
        Requires.NotNull(module);
        Requires.NotNull(enabledOptimizations);

        LirModule current = module;
        int iterations = Math.Max(1, maxIterations);
        for (int i = 0; i < iterations; i++)
        {
            bool changed = false;
            foreach (LirOptimization optimization in enabledOptimizations)
            {
                LirModule? result = optimization.Run(current);
                if (result is not null)
                {
                    current = result;
                    changed = true;
                }
            }

            if (!changed)
                break;
        }

        return current;
    }
}
