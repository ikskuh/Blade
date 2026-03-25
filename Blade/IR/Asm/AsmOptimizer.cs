using System.Collections.Generic;

namespace Blade.IR.Asm;

public static class AsmOptimizer
{
    public static AsmModule Optimize(AsmModule module, IReadOnlyList<AsmOptimization> enabledOptimizations)
    {
        Requires.NotNull(module);
        Requires.NotNull(enabledOptimizations);

        AsmModule current = module;
        bool changed;
        do
        {
            changed = false;
            foreach (AsmOptimization optimization in enabledOptimizations)
            {
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
