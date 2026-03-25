using System.Collections.Generic;

namespace Blade.IR;

public sealed class IrPipelineOptions
{
    public bool EnableSingleCallsiteInlining { get; init; } = true;
    public bool EnableMirInlining { get; init; } = true;
    public bool EnableMirOptimizations { get; init; } = true;
    public bool EnableLirOptimizations { get; init; } = true;
    public int MaxOptimizationIterations { get; init; } = 4;
    public IReadOnlyList<IMirOptimization> EnabledMirOptimizations { get; init; } = OptimizationRegistry.AllMirOptimizations;
    public IReadOnlyList<ILirOptimization> EnabledLirOptimizations { get; init; } = OptimizationRegistry.AllLirOptimizations;
    public IReadOnlyList<IAsmOptimization> EnabledAsmirOptimizations { get; init; } = OptimizationRegistry.AllAsmOptimizations;
}
