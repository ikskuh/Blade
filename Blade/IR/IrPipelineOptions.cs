namespace Blade.IR;

public sealed class IrPipelineOptions
{
    public bool EnableSingleCallsiteInlining { get; init; } = true;
    public bool EnableMirInlining { get; init; } = true;
    public bool EnableMirOptimizations { get; init; } = true;
    public int MaxOptimizationIterations { get; init; } = 4;
}
