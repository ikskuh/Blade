namespace Blade.IR.Mir.Optimizations;

[MirOptimization("cost-inline", Priority = 950)]
public sealed class MirCostInline : IMirOptimization
{
    public MirModule? Run(MirModule input)
    {
        Requires.NotNull(input);

        MirModule result = MirInliner.InlineCostBased(input, inlineCostThreshold: 12);
        return result.StructurallyEqualTo(input) ? null : result;
    }
}
