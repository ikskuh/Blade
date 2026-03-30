using System.Collections.Generic;

namespace Blade.IR;

public sealed class IrPipelineOptions
{
    public bool EnableSingleCallsiteInlining { get; init; } = true;
    public bool EnableMirInlining { get; init; } = true;
    public bool EnableMirOptimizations { get; init; } = true;
    public bool EnableLirOptimizations { get; init; } = true;
    public int MaxOptimizationIterations { get; init; } = 4;
    public IReadOnlyList<MirOptimization> EnabledMirOptimizations { get; init; } = OptimizationRegistry.AllMirOptimizations;
    public IReadOnlyList<LirOptimization> EnabledLirOptimizations { get; init; } = OptimizationRegistry.AllLirOptimizations;
    public IReadOnlyList<AsmOptimization> EnabledAsmirOptimizations { get; init; } = OptimizationRegistry.AllAsmOptimizations;
    public RuntimeTemplate? RuntimeTemplate { get; init; }
}
