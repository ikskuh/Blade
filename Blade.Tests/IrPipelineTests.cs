using Blade.Diagnostics;
using Blade.IR;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;
using Blade.Syntax;
using Blade.Syntax.Nodes;

namespace Blade.Tests;

[TestFixture]
public class IrPipelineTests
{
    private static (BoundProgram Program, DiagnosticBag Diagnostics) Bind(string text)
    {
        SourceText source = new(text);
        DiagnosticBag diagnostics = new();
        Parser parser = Parser.Create(source, diagnostics);
        CompilationUnitSyntax unit = parser.ParseCompilationUnit();
        BoundProgram program = Binder.Bind(unit, diagnostics);
        return (program, diagnostics);
    }

    [Test]
    public void StageWriters_EmitVersionHeaders()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            inline fn inc(x: u32) -> u32 {
                return x + 1;
            }

            reg var x: u32 = inc(1);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
        });

        string mir = MirTextWriter.Write(build.MirModule);
        string lir = LirTextWriter.Write(build.LirModule);
        string asmir = AsmTextWriter.Write(build.AsmModule);

        Assert.That(mir, Does.StartWith("; MIR v1"));
        Assert.That(lir, Does.StartWith("; LIR v1"));
        Assert.That(asmir, Does.StartWith("; ASMIR v2"));
        Assert.That(build.AssemblyText, Does.StartWith("DAT"));
    }

    [Test]
    public void MirWriter_IsDeterministicAcrossRuns()
    {
        const string source = """
            fn add(a: u32, b: u32) -> u32 {
                return a + b;
            }

            reg var x: u32 = add(1, 2);
            if (x == 3) {
                x = x + 1;
            } else {
                x = x + 2;
            }
            """;

        (BoundProgram program, DiagnosticBag diagnostics) = Bind(source);
        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult first = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
        });
        IrBuildResult second = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
        });

        Assert.That(MirTextWriter.Write(first.MirModule), Is.EqualTo(MirTextWriter.Write(second.MirModule)));
        Assert.That(LirTextWriter.Write(first.LirModule), Is.EqualTo(LirTextWriter.Write(second.LirModule)));
        Assert.That(AsmTextWriter.Write(first.AsmModule), Is.EqualTo(AsmTextWriter.Write(second.AsmModule)));
    }

    [Test]
    public void InlineFunction_IsAlwaysInlined()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            inline fn add1(x: u32) -> u32 {
                return x + 1;
            }

            fn apply(v: u32) -> u32 {
                return add1(v);
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableSingleCallsiteInlining = false,
            EnableMirOptimizations = false,
        });

        string mir = MirTextWriter.Write(build.MirModule);
        Assert.That(mir, Does.Not.Contain("call add1("));
    }

    [Test]
    public void SingleCallsiteInlining_CanBeDisabled()
    {
        const string source = """
            fn helper(x: u32) -> u32 {
                return x + 1;
            }

            fn apply(v: u32) -> u32 {
                return helper(v);
            }
            """;

        (BoundProgram program, DiagnosticBag diagnostics) = Bind(source);
        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult withoutInlining = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableSingleCallsiteInlining = false,
            EnableMirOptimizations = false,
        });
        IrBuildResult withInlining = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableSingleCallsiteInlining = true,
            EnableMirOptimizations = false,
        });

        string withoutMir = MirTextWriter.Write(withoutInlining.MirModule);
        string withMir = MirTextWriter.Write(withInlining.MirModule);
        Assert.That(withoutMir, Does.Contain("call helper("));
        Assert.That(withMir, Does.Not.Contain("call helper("));
    }
}
