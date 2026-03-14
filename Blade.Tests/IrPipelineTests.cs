using System.Text.RegularExpressions;
using Blade;
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

        Assert.That(build.AssemblyText, Does.Match(@"MOV\s+_r\d+,\s+_r\d+"));
        Assert.That(build.AssemblyText, Does.Match(@"TESTB\s+_r\d+,\s+_r\d+\s+WC"));
        Assert.That(build.AssemblyText, Does.Match(@"IF_NC BITH\s+_r\d+,\s+_r\d+"));
        Assert.That(build.AssemblyText, Does.Not.Contain("MOV   out, val"));
        Assert.That(build.AssemblyText, Does.Not.Contain("TESTB out, bit_num WC"));
        Assert.That(build.AssemblyText, Does.Not.Contain("%r"));
    }

    [Test]
    public void InlineAsmVolatile_SurvivesMirAndLir()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn f(x: u32) -> u32 {
                var out: u32 = 0;
                asm volatile {
                    MOV   {out}, {x}
                };
                return out;
            }

            reg var sink: u32 = f(1);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableSingleCallsiteInlining = false,
            EnableMirInlining = false,
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(MirTextWriter.Write(build.MirModule), Does.Contain("inlineasm.volatile"));
        Assert.That(LirTextWriter.Write(build.LirModule), Does.Contain("inlineasm.volatile"));
        AsmFunction function = build.AsmModule.Functions.Single(f => f.Name == "f");
        Assert.That(function.Nodes.OfType<AsmInlineTextNode>().Any(), Is.True);
    }

    [Test]
    public void InlineAsm_NonVolatile_LowersToTypedAsmInstructions()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn f(x: u32) -> u32 {
                var out: u32 = 0;
                asm {
                    MOV {out}, {x}
                    ADD {out}, #1
                };
                return out;
            }

            reg var sink: u32 = f(1);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableSingleCallsiteInlining = false,
            EnableMirInlining = false,
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        AsmFunction function = build.AsmModule.Functions.Single(f => f.Name == "f");
        Assert.That(function.Nodes.OfType<AsmInstructionNode>().Any(i => i.Opcode == "ADD"), Is.True);
        Assert.That(function.Nodes.OfType<AsmInlineTextNode>().Any(), Is.False);
        Assert.That(AsmTextWriter.Write(build.AsmModule), Does.Contain("inline asm typed begin"));
    }

    [Test]
    public void InlineAsm_GeneralTierReturnValue_StaysLiveThroughAsmOptimization()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn leaf_add(x: u32) -> u32 {
                return x + 1;
            }

            fn mid(x: u32) -> u32 {
                return leaf_add(x);
            }

            fn f(x: u32) -> u32 {
                var ignored: u32 = mid(x);
                var out: u32 = 0;
                asm {
                    MOV {out}, {x}
                    ADD {out}, #1
                };
                return out;
            }

            reg var sink: u32 = f(1);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableSingleCallsiteInlining = false,
            EnableMirInlining = false,
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        AsmFunction function = build.AsmModule.Functions.Single(f => f.Name == "f");
        Assert.That(function.CcTier, Is.EqualTo(CallingConventionTier.General));
        Assert.That(function.Nodes.OfType<AsmInstructionNode>().Any(i => i.Opcode == "ADD"), Is.True);
        Assert.That(function.Nodes.OfType<AsmImplicitUseNode>().Any(), Is.True);
    }

    [Test]
    public void InlineAsm_Volatile_TransposesCommentsToPasmStyle()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn f(x: u32) -> u32 {
                var out: u32 = 0;
                asm volatile {
                    // keep this comment
                    MOV   {out}, {x}
                };
                return out;
            }

            reg var sink: u32 = f(1);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableSingleCallsiteInlining = false,
            EnableMirInlining = false,
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Contain("' keep this comment"));
        Assert.That(build.AssemblyText, Does.Not.Contain("// keep this comment"));
        Assert.That(build.AssemblyText, Does.Match(@"MOV   \s*_r\d+,\s+_r\d+"));
    }

    [Test]
    public void InlineAsm_NonVolatile_PreservesAndTransposesComments()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn f(x: u32) -> u32 {
                var out: u32 = 0;
                asm {
                    // before
                    MOV {out}, {x} // after
                };
                return out;
            }

            reg var sink: u32 = f(1);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableSingleCallsiteInlining = false,
            EnableMirInlining = false,
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Contain("' before"));
        Assert.That(build.AssemblyText, Does.Contain("' after"));
        Assert.That(build.AssemblyText, Does.Match(@"MOV\s+_r\d+,\s+_r\d+"));
    }

    [Test]
    public void InlineAsm_NonVolatile_FallsBackToRawWhenOperandShapeIsUnsupported()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn f(x: u32) -> u32 {
                var out: u32 = 0;
                asm {
                    MOV {out}, #target_label // keep raw fallback comment
                };
                return out;
            }

            reg var sink: u32 = f(1);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableSingleCallsiteInlining = false,
            EnableMirInlining = false,
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        AsmFunction function = build.AsmModule.Functions.Single(f => f.Name == "f");
        Assert.That(function.Nodes.OfType<AsmInlineTextNode>().Any(), Is.True);
        Assert.That(build.AssemblyText, Does.Contain("#target_label"));
        Assert.That(build.AssemblyText, Does.Contain("' keep raw fallback comment"));
    }

    [Test]
    public void InlineAsm_FlagOutput_StaysOpaqueForOptimizationSafety()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn test_bit(val: u32, pos: u32) -> bool@C {
                asm -> @C {
                    TESTB {val}, {pos} WC
                };
            }

            reg var sink: bool = test_bit(0, 1);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableSingleCallsiteInlining = false,
            EnableMirInlining = false,
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(MirTextWriter.Write(build.MirModule), Does.Contain("inlineasm -> @C"));
        Assert.That(LirTextWriter.Write(build.LirModule), Does.Contain("inlineasm -> @C"));
        AsmFunction function = build.AsmModule.Functions.Single(f => f.Name == "test_bit");
        Assert.That(function.Nodes.OfType<AsmInlineTextNode>().Any(), Is.True);
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
        string[] duplicateMirLabels = build.MirModule.Functions
            .SelectMany(f => f.Blocks.Select(block => $"{f.Name}:{block.Label}"))
            .GroupBy(label => label)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        string[] duplicateAsmLabels = build.AsmModule.Functions
            .SelectMany(f => f.Nodes.OfType<AsmLabelNode>())
            .GroupBy(label => label.Name)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.That(mir, Does.Not.Contain("inl_0_bb1"));
        Assert.That(mir, Does.Not.Contain("inl_after_0"));
        Assert.That(lir, Does.Not.Contain("inl_0_bb1"));
        Assert.That(lir, Does.Not.Contain("inl_after_0"));
        Assert.That(duplicateMirLabels, Is.Empty);
        Assert.That(duplicateAsmLabels, Is.Empty);

        Assert.That(build.AssemblyText, Does.Match(@"MOV\s+g_flags_\d+,\s+_r\d+"));
        Assert.That(build.AssemblyText, Does.Not.Contain("%r"));
    }

    [Test]
    public void ReservedFixedRegisterAlias_UsesDirectOperandWithoutConAlias_AndKeepsAddressBinding()
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

        StoragePlace place = build.AsmModule.StoragePlaces.Single(p => p.EmittedName == "OUTA");
        Assert.That(place.Kind, Is.EqualTo(StoragePlaceKind.FixedRegisterAlias));
        Assert.That(place.FixedAddress, Is.EqualTo(0x1FC));
        Assert.That(build.AssemblyText, Does.Contain("OR OUTA, #16"));
        Assert.That(build.AssemblyText, Does.Not.Contain("OUTA = 0x1FC"));
        Assert.That(build.AssemblyText, Does.Not.Contain("LONG 0"));
        Assert.That(build.AssemblyText, Does.Not.Contain("g_OUTA"));
    }

    [Test]
    public void NonReservedFixedRegisterAlias_StillEmitsConAlias()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            extern reg var LED_PORT: u32 @(0x1FC);
            LED_PORT |= 0x10;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
        });

        StoragePlace place = build.AsmModule.StoragePlaces.Single(p => p.EmittedName == "LED_PORT");
        Assert.That(place.FixedAddress, Is.EqualTo(0x1FC));
        Assert.That(build.AssemblyText, Does.Contain("CON"));
        Assert.That(build.AssemblyText, Does.Contain("LED_PORT = 0x1FC"));
        Assert.That(build.AssemblyText, Does.Contain("OR LED_PORT, #16"));
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

    [Test]
    public void RegisterAllocator_LeafFunctions_ShareRegisterSlots()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn add_one(x: u32) -> u32 {
                return x + 1;
            }

            fn add_two(x: u32) -> u32 {
                return x + 2;
            }

            var a: u32 = add_one(5);
            var b: u32 = add_two(10);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = true,
            EnableLirOptimizations = true,
        });

        // Both leaf functions should share register slots, resulting in fewer
        // LONG 0 entries than if each function had its own registers
        int longZeroCount = Regex.Matches(build.AssemblyText, @"LONG 0").Count;

        // With sharing, the total should be small — two leaf functions with 1 temp each
        // should share the same slot
        Assert.That(longZeroCount, Is.LessThanOrEqualTo(4),
            $"Expected register sharing to reduce LONG 0 count.\nAssembly:\n{build.AssemblyText}");
        Assert.That(build.AssemblyText, Does.Not.Contain("%r"));
    }

    [Test]
    public void RegisterAllocator_NoVirtualRegistersInOutput()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn double(x: u32) -> u32 {
                return x + x;
            }

            reg var result: u32 = double(42);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Not.Contain("%r"),
            "No virtual registers should remain in final output");
        Assert.That(build.AssemblyText, Does.Match(@"_r\d+"),
            "Physical register slots should be present");
    }

    [Test]
    public void RegisterAllocator_UsesSharedSlotLabels()
    {
        // Use a program complex enough that virtual registers survive optimization
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
            EnableMirOptimizations = false,
        });

        // Register slots should use the new _rN naming scheme
        Assert.That(build.AssemblyText, Does.Match(@"_r\d+"));
        Assert.That(build.AssemblyText, Does.Not.Match(@"_top_r\d+"),
            "Old per-function register naming should not appear");
    }
}
