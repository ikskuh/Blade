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

            var x: u32 = inc(1);
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

            var x: u32 = add(1, 2);
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

    [Test]
    public void InlineAsm_PlaceholdersResolveToAllocatedSymbolsInFinalAssembly()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn test_and_set_bit(val: u32, bit_num: u32) -> u32 {
                var out: u32 = 0;
                asm {
                    MOV   {out}, {val}
                    TESTB {out}, {bit_num} WC
                    IF_NC BITH  {out}, {bit_num}
                };
                return out;
            }

            reg var flags: u32 = test_and_set_bit(0, 5);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Match(@"MOV\s+_top_r\d+,\s+_top_r\d+"));
        Assert.That(build.AssemblyText, Does.Match(@"TESTB\s+_top_r\d+,\s+_top_r\d+\s+WC"));
        Assert.That(build.AssemblyText, Does.Match(@"IF_NC BITH\s+_top_r\d+,\s+_top_r\d+"));
        Assert.That(build.AssemblyText, Does.Not.Contain("MOV   out, val"));
        Assert.That(build.AssemblyText, Does.Not.Contain("TESTB out, bit_num WC"));
        Assert.That(build.AssemblyText, Does.Not.Contain("%r"));
    }

    [Test]
    public void InlineAsm_CopyAndJumpElision_CollapsesReturnHopChains()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var flags: u32 = 0;

            fn test_and_set_bit(val: u32, bit_num: u32) -> u32 {
                var out: u32 = 0;
                asm {
                    MOV   {out}, {val}
                    TESTB {out}, {bit_num} WC
                    IF_NC BITH  {out}, {bit_num}
                };
                return out;
            }

            flags = test_and_set_bit(flags, 5);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = true,
            EnableLirOptimizations = true,
        });

        string mir = MirTextWriter.Write(build.MirModule);
        string lir = LirTextWriter.Write(build.LirModule);

        Assert.That(mir, Does.Not.Contain("inl_0_bb1"));
        Assert.That(mir, Does.Not.Contain("inl_after_0"));
        Assert.That(lir, Does.Not.Contain("inl_0_bb1"));
        Assert.That(lir, Does.Not.Contain("inl_after_0"));

        Assert.That(build.AssemblyText, Does.Match(@"MOV\s+g_flags_\d+,\s+_top_r\d+"));
        Assert.That(build.AssemblyText, Does.Not.Contain("$top_inl_0_bb1"));
        Assert.That(build.AssemblyText, Does.Not.Contain("$top_inl_after_0"));
        Assert.That(build.AssemblyText, Does.Not.Contain("%r"));
    }

    [Test]
    public void FixedRegisterAlias_UsesConAliasAndDirectOperand()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            extern reg var OUTA: u32 @(0x1FC);
            OUTA |= 0x10;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Contain("CON"));
        Assert.That(build.AssemblyText, Does.Contain("OUTA = 0x1FC"));
        Assert.That(build.AssemblyText, Does.Contain("OR OUTA, #16"));
        Assert.That(build.AssemblyText, Does.Not.Contain("LONG 0"));
        Assert.That(build.AssemblyText, Does.Not.Contain("g_OUTA"));
    }

    [Test]
    public void ExternalRegisterAliasWithoutAddress_StaysAsBareSymbol()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            extern reg var FOO: u32;
            FOO = 1;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Contain("MOV FOO, #1"));
        Assert.That(build.AssemblyText, Does.Not.Contain("LONG 0"));
        Assert.That(build.AssemblyText, Does.Not.Contain("g_FOO"));
    }

    [Test]
    public void AllocatableGlobalStaticInitializer_EmitsLongData()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var g: u32 = 1000;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Match(@"g_g_\d+"));
        Assert.That(build.AssemblyText, Does.Contain("LONG 1000"));
        Assert.That(build.AssemblyText, Does.Not.Contain("MOV g_g_"));
    }
}
