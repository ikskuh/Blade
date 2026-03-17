using System;
using Blade.IR;

namespace Blade.Tests;

[TestFixture]
public sealed class CompilationOptionsCommandLineTests
{
    [Test]
    public void IsCompilationOption_RecognizesSupportedPrefixes()
    {
        Assert.That(CompilationOptionsCommandLine.IsCompilationOption("-fmir-opt=const-prop"), Is.True);
        Assert.That(CompilationOptionsCommandLine.IsCompilationOption("-fno-lir-opt=dce"), Is.True);
        Assert.That(CompilationOptionsCommandLine.IsCompilationOption("-fasmir-opt=*"), Is.True);
        Assert.That(CompilationOptionsCommandLine.IsCompilationOption("--module=extmod=mods/ext.blade"), Is.True);
        Assert.That(CompilationOptionsCommandLine.IsCompilationOption("--dump-bound"), Is.False);
        Assert.That(CompilationOptionsCommandLine.IsCompilationOption("input.blade"), Is.False);
    }

    [Test]
    public void TryParse_ParsesOptimizationAndModuleArgumentsRelativeToBaseDirectory()
    {
        using TempDirectory tempDirectory = new();

        bool succeeded = CompilationOptionsCommandLine.TryParse(
            [
                "-fno-mir-opt=const-prop",
                "-flir-opt=dce",
                "--module=extmod=mods/ext.blade",
            ],
            tempDirectory.Path,
            out CompilationOptions options,
            out string? errorMessage);

        Assert.That(succeeded, Is.True);
        Assert.That(errorMessage, Is.Null);
        Assert.That(options.EnableSingleCallsiteInlining, Is.True);
        Assert.That(options.OptimizationDirectives, Has.Count.EqualTo(2));
        Assert.That(options.OptimizationDirectives[0].Stage, Is.EqualTo(OptimizationStage.Mir));
        Assert.That(options.OptimizationDirectives[0].Enable, Is.False);
        Assert.That(options.OptimizationDirectives[0].Names, Is.EqualTo(["const-prop"]));
        Assert.That(options.OptimizationDirectives[1].Stage, Is.EqualTo(OptimizationStage.Lir));
        Assert.That(options.OptimizationDirectives[1].Enable, Is.True);
        Assert.That(options.OptimizationDirectives[1].Names, Is.EqualTo(["dce"]));
        Assert.That(options.NamedModuleRoots.Keys, Is.EqualTo(["extmod"]));
        Assert.That(options.NamedModuleRoots["extmod"], Is.EqualTo(tempDirectory.GetFullPath("mods/ext.blade")));
    }

    [Test]
    public void Parse_InvalidArgument_ThrowsSharedValidationError()
    {
        using TempDirectory tempDirectory = new();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            CompilationOptionsCommandLine.Parse(["-fmir-opt="], tempDirectory.Path))!;

        Assert.That(exception.Message, Does.Contain("missing optimization list"));
    }

    [Test]
    public void TryParse_RejectsUnsupportedCompilerOption()
    {
        using TempDirectory tempDirectory = new();

        bool succeeded = CompilationOptionsCommandLine.TryParse(
            ["--bogus-compile-option"],
            tempDirectory.Path,
            out CompilationOptions options,
            out string? errorMessage);

        Assert.That(succeeded, Is.False);
        Assert.That(errorMessage, Is.EqualTo("error: unsupported compiler option '--bogus-compile-option'."));
        Assert.That(options.OptimizationDirectives, Is.Empty);
        Assert.That(options.NamedModuleRoots.Count, Is.EqualTo(0));
    }
}
