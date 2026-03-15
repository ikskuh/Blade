using Blade.IR;

namespace Blade.Tests;

[TestFixture]
public class OptimizationSelectionTests
{
    [Test]
    public void ResolveEnabled_AppliesDirectivesInOrder()
    {
        OptimizationDirective[] directives =
        [
            new(OptimizationStage.Asmir, Enable: false, ["*"]),
            new(OptimizationStage.Asmir, Enable: true, ["elide-nops"]),
        ];

        IReadOnlyList<string> enabled = OptimizationCatalog.ResolveEnabled(OptimizationStage.Asmir, directives);
        Assert.That(enabled, Is.EqualTo(new[] { "elide-nops" }));
    }

    [Test]
    public void ResolveEnabled_LeavesOtherStagesUntouched()
    {
        OptimizationDirective[] directives =
        [
            new(OptimizationStage.Mir, Enable: false, ["const-prop"]),
        ];

        IReadOnlyList<string> enabled = OptimizationCatalog.ResolveEnabled(OptimizationStage.Lir, directives);
        Assert.That(enabled, Is.EqualTo(OptimizationCatalog.LirDefaultOrder));
    }
}
