using System;
using System.IO;
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
        Assert.That(CompilationOptionsCommandLine.IsCompilationOption("--comptime-fuel=123"), Is.True);
        Assert.That(CompilationOptionsCommandLine.IsCompilationOption("--module=extmod=mods/ext.blade"), Is.True);
        Assert.That(CompilationOptionsCommandLine.IsCompilationOption("--runtime=runtime.blade"), Is.True);
        Assert.That(CompilationOptionsCommandLine.IsCompilationOption("--dump-bound"), Is.False);
        Assert.That(CompilationOptionsCommandLine.IsCompilationOption("input.blade"), Is.False);
    }

    [Test]
    public void TryParse_UsesDefaultComptimeFuelWhenNoOverrideIsPresent()
    {
        using TempDirectory tempDirectory = new();

        bool succeeded = CompilationOptionsCommandLine.TryParse(
            [],
            tempDirectory.Path,
            out CompilationOptions options,
            out string? errorMessage);

        Assert.That(succeeded, Is.True);
        Assert.That(errorMessage, Is.Null);
        Assert.That(options.ComptimeFuel, Is.EqualTo(250));
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
        Assert.That(options.EnabledMirOptimizations, Is.EqualTo(OptimizationRegistry.AllMirOptimizations));
        Assert.That(options.NamedModuleRoots.Count, Is.EqualTo(0));
    }

    [Test]
    public void TryParse_RejectsBuiltinModuleName()
    {
        using TempDirectory tempDirectory = new();

        bool succeeded = CompilationOptionsCommandLine.TryParse(
            ["--module=builtin=mods/ext.blade"],
            tempDirectory.Path,
            out CompilationOptions options,
            out string? errorMessage);

        Assert.That(succeeded, Is.False);
        Assert.That(errorMessage, Is.EqualTo("error: module name 'builtin' is reserved for the compiler-provided builtin module."));
        Assert.That(options.NamedModuleRoots.Count, Is.EqualTo(0));
    }

    [Test]
    public void TryParse_ResolvesRuntimeLauncherRelativeToBaseDirectory()
    {
        using TempDirectory tempDirectory = new();
        string runtimeDirectory = Path.Combine(tempDirectory.Path, "runtimes");
        Directory.CreateDirectory(runtimeDirectory);
        string runtimePath = Path.Combine(runtimeDirectory, "basic.blade");
        File.WriteAllText(runtimePath, "import builtin;\ncog task _start { builtin.task_main(); }\n");

        bool succeeded = CompilationOptionsCommandLine.TryParse(
            ["--runtime=runtimes/basic.blade"],
            tempDirectory.Path,
            out CompilationOptions options,
            out string? errorMessage);

        Assert.That(succeeded, Is.True);
        Assert.That(errorMessage, Is.Null);
        Assert.That(options.RuntimeLauncherPath, Is.EqualTo(runtimePath));
    }

    [Test]
    public void TryParse_RejectsDuplicateRuntimeLauncherSpecification()
    {
        using TempDirectory tempDirectory = new();
        string runtimePath = Path.Combine(tempDirectory.Path, "basic.blade");
        File.WriteAllText(runtimePath, "import builtin;\ncog task _start { builtin.task_main(); }\n");

        bool succeeded = CompilationOptionsCommandLine.TryParse(
            ["--runtime=basic.blade", "--runtime=basic.blade"],
            tempDirectory.Path,
            out CompilationOptions options,
            out string? errorMessage);

        Assert.That(succeeded, Is.False);
        Assert.That(errorMessage, Is.EqualTo("error: duplicate runtime launcher specification."));
        Assert.That(options.RuntimeLauncherPath, Is.Null);
    }

    [Test]
    public void TryParse_RejectsNonBladeRuntimeLauncher()
    {
        using TempDirectory tempDirectory = new();

        bool succeeded = CompilationOptionsCommandLine.TryParse(
            ["--runtime=basic.spin2"],
            tempDirectory.Path,
            out CompilationOptions options,
            out string? errorMessage);

        Assert.That(succeeded, Is.False);
        Assert.That(errorMessage, Is.EqualTo($"error: runtime launcher '{Path.Combine(tempDirectory.Path, "basic.spin2")}' must be a .blade file."));
        Assert.That(options.RuntimeLauncherPath, Is.Null);
    }

    [TestCase("--comptime-fuel=0", "0")]
    [TestCase("--comptime-fuel=-1", "-1")]
    [TestCase("--comptime-fuel=abc", "abc")]
    public void TryParse_RejectsInvalidComptimeFuel(string arg, string payload)
    {
        using TempDirectory tempDirectory = new();

        bool succeeded = CompilationOptionsCommandLine.TryParse(
            [arg],
            tempDirectory.Path,
            out CompilationOptions options,
            out string? errorMessage);

        Assert.That(succeeded, Is.False);
        Assert.That(errorMessage, Is.EqualTo($"error: invalid comptime fuel '{payload}'. Expected a positive integer."));
        Assert.That(options.ComptimeFuel, Is.EqualTo(250));
    }
}
