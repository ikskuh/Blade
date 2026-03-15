using System.Collections.Generic;

namespace Blade.IR;

public sealed class EmitOptions
{
    public bool EnableAsmOptimization { get; init; } = true;
    public IReadOnlyList<string> EnabledAsmirOptimizations { get; init; } = OptimizationCatalog.AsmirDefaultOrder;
    public bool EnableRegisterAllocation { get; init; } = true;
    public bool EnableLegalization { get; init; } = true;
}
