namespace Blade.IR.Mir.Optimizations;

[MirOptimization("single-callsite-inline", Priority = 1000)]
public sealed class MirSingleCallsiteInline : IMirOptimization
{
    public MirModule? Run(MirModule input) => null;
}
