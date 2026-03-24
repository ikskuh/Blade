using System;
using System.Collections.Generic;

namespace Blade.IR.Asm;

public static class AsmOptimizer
{
    public static AsmModule Optimize(AsmModule module, IReadOnlyList<string> enabledOptimizations)
    {
        Requires.NotNull(module);
        Requires.NotNull(enabledOptimizations);

        AsmModule current = module;
        bool changed;
        do
        {
            changed = false;
            foreach (string name in enabledOptimizations)
            {
                IAsmOptimization optimization = OptimizationRegistry.GetAsmOptimization(name);
                AsmModule? result = optimization.Run(current);
                if (result is not null)
                {
                    current = result;
                    changed = true;
                }
            }
        }
        while (changed);

        return current;
    }
}
