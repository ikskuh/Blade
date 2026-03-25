using Blade.IR;

namespace Blade.Tests;

[TestFixture]
public class OptimizationSelectionTests
{
    [Test]
    public void TryParse_DisableAllThenEnableOne_ResolvesToSingleOptimization()
    {
        using TempDirectory tempDirectory = new();

        bool succeeded = CompilationOptionsCommandLine.TryParse(
            ["-fno-asmir-opt=*", "-fasmir-opt=elide-nops"],
            tempDirectory.Path,
            out CompilationOptions options,
            out string? errorMessage);

        Assert.That(succeeded, Is.True);
        Assert.That(errorMessage, Is.Null);
        Assert.That(options.EnabledAsmirOptimizations, Has.Count.EqualTo(1));
        Assert.That(options.EnabledAsmirOptimizations[0], Is.TypeOf(OptimizationRegistry.GetAsmOptimization("elide-nops")!.GetType()));
    }

    [Test]
    public void TryParse_MirDirectiveDoesNotAffectLirOptimizations()
    {
        using TempDirectory tempDirectory = new();

        bool succeeded = CompilationOptionsCommandLine.TryParse(
            ["-fno-mir-opt=const-prop"],
            tempDirectory.Path,
            out CompilationOptions options,
            out string? errorMessage);

        Assert.That(succeeded, Is.True);
        Assert.That(errorMessage, Is.Null);
        Assert.That(options.EnabledLirOptimizations, Is.EqualTo(OptimizationRegistry.AllLirOptimizations));
    }
}
