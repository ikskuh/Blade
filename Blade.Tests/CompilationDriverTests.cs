using System;
using System.Linq;
using Blade;
using Blade.Diagnostics;

namespace Blade.Tests;

[TestFixture]
public sealed class CompilationDriverTests
{
    [Test]
    public void ImportedModuleParseError_IsAttributedToImportedFileAndSkipsBinder()
    {
        using TempDirectory temp = new();
        temp.WriteFile("bad.blade", "fn () void { }");

        string mainPath = temp.GetFullPath("main.blade");
        temp.WriteFile("main.blade", """import "./bad.blade" as bad; fn ok() void { }""");

        CompilationResult result = CompilerDriver.CompileFile(mainPath, new CompilationOptions
        {
            EmitIr = false,
        });

        Assert.That(result.Diagnostics.Count, Is.GreaterThan(0));
        Assert.That(result.Diagnostics.Any(d => d.IsError), Is.True);
        Assert.That(result.BoundProgram.Functions, Has.Count.EqualTo(1), "Failed compilations still expose the synthetic empty constructor only.");
        Assert.That(result.BoundProgram.Functions.Single().Symbol.IsTopLevel, Is.True);

        Diagnostic first = result.Diagnostics.First();
        Assert.That(first.Source.FilePath, Is.EqualTo(temp.GetFullPath("bad.blade")));
        Assert.That(first.GetLocation().FilePath, Is.EqualTo(temp.GetFullPath("bad.blade")));
    }

    [Test]
    public void CircularImports_AreRejectedInBinderAndAttributedToImportSiteSource()
    {
        using TempDirectory temp = new();

        temp.WriteFile("a.blade", """import "./b.blade" as b; fn a() void { }""");
        temp.WriteFile("b.blade", """import "./a.blade" as a; fn b() void { }""");

        CompilationResult result = CompilerDriver.CompileFile(temp.GetFullPath("a.blade"), new CompilationOptions
        {
            EmitIr = false,
        });

        Assert.That(result.Diagnostics.Any(d => d.Code == DiagnosticCode.E0231_CircularImport), Is.True);

        Diagnostic cycle = result.Diagnostics.First(d => d.Code == DiagnosticCode.E0231_CircularImport);
        Assert.That(cycle.Source.FilePath, Is.EqualTo(temp.GetFullPath("b.blade")));
        Assert.That(cycle.GetLocation().FilePath, Is.EqualTo(temp.GetFullPath("b.blade")));
    }
}
