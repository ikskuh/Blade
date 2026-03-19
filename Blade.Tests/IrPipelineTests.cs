using System.IO;
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
        BoundProgram program = Binder.Bind(unit, diagnostics, source.FilePath, null);
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
    public void DumpContentBuilder_CanEmitPreOptimizationStageDumps()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn add_one(x: u32) -> u32 {
                var copy: u32 = x;
                return copy + 1;
            }

            var sink: u32 = add_one(1);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = true,
            EnableLirOptimizations = true,
        });

        DumpSelection selection = new()
        {
            DumpMirPreOptimization = true,
            DumpMir = true,
            DumpLirPreOptimization = true,
            DumpLir = true,
            DumpAsmirPreOptimization = true,
            DumpAsmir = true,
        };
        Dictionary<string, string> dumps = DumpContentBuilder.Build(selection, build);

        Assert.That(dumps.Keys, Is.EquivalentTo(new[]
        {
            "05_mir_preopt.ir",
            "10_mir.ir",
            "15_lir_preopt.ir",
            "20_lir.ir",
            "25_asmir_preopt.ir",
            "30_asmir.ir",
        }));
        Assert.That(dumps["05_mir_preopt.ir"], Is.EqualTo(MirTextWriter.Write(build.PreOptimizationMirModule)));
        Assert.That(dumps["10_mir.ir"], Is.EqualTo(MirTextWriter.Write(build.MirModule)));
        Assert.That(dumps["15_lir_preopt.ir"], Is.EqualTo(LirTextWriter.Write(build.PreOptimizationLirModule)));
        Assert.That(dumps["20_lir.ir"], Is.EqualTo(LirTextWriter.Write(build.LirModule)));
        Assert.That(dumps["25_asmir_preopt.ir"], Is.EqualTo(AsmTextWriter.Write(build.PreOptimizationAsmModule)));
        Assert.That(dumps["30_asmir.ir"], Is.EqualTo(AsmTextWriter.Write(build.AsmModule)));
    }

    [Test]
    public void BitcastToSignedByte_DoesNotEmitNegativeImmediateLiteral()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var reinterpreted_signed: i8 = 0;
            reinterpreted_signed = bitcast(i8, 255 as u8);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        IrBuildResult build = IrPipeline.Build(program);

        Assert.That(build.AssemblyText, Does.Not.Contain("MOV g_reinterpreted_signed, #-1"));
        Assert.That(build.AssemblyText, Does.Contain("MOV g_reinterpreted_signed, #511"));
    }

    [Test]
    public void InlineAsm_InlinedVolatileBindingValue_RemainsLiveThroughOptimization()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn consume(x: u32) {
                asm volatile {
                    MOV INA, {x}
                };
            }

            consume(13);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        IrBuildResult build = IrPipeline.Build(program);

        string lir = LirTextWriter.Write(build.LirModule);
        Assert.That(lir, Does.Match(@"const 13:[A-Za-z0-9_<>\-]+"));

        Match initMatch = Regex.Match(build.AssemblyText, @"MOV (_r\d+), #13");
        Match useMatch = Regex.Match(build.AssemblyText, @"MOV INA, (_r\d+)");
        Assert.That(initMatch.Success, Is.True, build.AssemblyText);
        Assert.That(useMatch.Success, Is.True, build.AssemblyText);
        Assert.That(initMatch.Groups[1].Value, Is.EqualTo(useMatch.Groups[1].Value), build.AssemblyText);
        Assert.That(initMatch.Index, Is.LessThan(useMatch.Index), build.AssemblyText);
    }

    [Test]
    public void InlineAsm_CopyChainInput_RemainsLiveThroughOptimization()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var input_word: u32 = 13;
            reg var copy_folded: u32 = 0;

            fn copy_chain(x: u32) -> u32 {
                var tmp0: u32 = 0;
                var tmp1: u32 = 0;
                var out: u32 = 0;
                asm {
                    MOV {tmp0}, {x}
                    MOV {tmp1}, {tmp0}
                    MOV {out}, {tmp1}
                };
                return out;
            }

            copy_folded = copy_chain(input_word);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        IrBuildResult build = IrPipeline.Build(program);

        Assert.That(LirTextWriter.Write(build.LirModule), Does.Contain("load.place %place(g_input_word"));
        Assert.That(build.AssemblyText, Does.Match(@"MOV g_copy_folded,\s+g_input_word"));
    }

    [Test]
    public void NamedArguments_BuildThroughIrPipeline()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn pair(x: u32, y: u32) -> u32 {
                return x + y;
            }

            var result: u32 = pair(10, y=20);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        string mir = MirTextWriter.Write(build.MirModule);
        string lir = LirTextWriter.Write(build.LirModule);

        Assert.That(mir, Does.Contain("const 10"));
        Assert.That(mir, Does.Contain("const 20"));
        Assert.That(mir, Does.Contain("fn pair"));
        Assert.That(lir, Does.Contain("binary.Add"));
    }

    [Test]
    public void ExplicitIntegerCasts_EmitExtensionInstructions()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            noinline fn demo(x: u32) -> u32 {
                var lo: u8 = x as u8;
                var hi: i8 = x as i8;
                return lo + (hi as u32);
            }

            var result: u32 = demo(255);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(MirTextWriter.Write(build.MirModule), Does.Contain("convert"));
        Assert.That(build.AssemblyText, Does.Contain("ZEROX"));
        Assert.That(build.AssemblyText, Does.Contain("SIGNX"));
    }

    [Test]
    public void ExplicitCast_StaticInitializer_IsNormalized()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var narrowed: u8 = 257 as u8;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Match(@"g_narrowed\s+LONG\s+1"));
    }

    [Test]
    public void Bitcast_LowersAsCopy()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            noinline fn demo(raw: u32) -> u32 {
                var ptr: *reg u32 = bitcast(*reg u32, raw);
                return bitcast(u32, ptr);
            }

            var result: u32 = demo(1);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        string mir = MirTextWriter.Write(build.MirModule);

        Assert.That(mir, Does.Contain("copy"));
    }

    [Test]
    public void Bitcast_StaticInitializer_ReinterpretsBits()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var signed: i8 = bitcast(i8, 255 as u8);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Match(@"g_signed\s+LONG\s+-1"));
    }

    [Test]
    public void LogicalOperators_LowerWithShortCircuitControlFlow()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn demo(a: bool, b: bool) -> bool {
                return (a and b) or a;
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
        });

        string mir = MirTextWriter.Write(build.MirModule);

        Assert.That(mir, Does.Contain("branch "));
        Assert.That(mir, Does.Not.Contain("binary.LogicalAnd"));
        Assert.That(mir, Does.Not.Contain("binary.LogicalOr"));
    }

    [Test]
    public void NewIntegerOperators_EmitExpectedAssemblyInstructions()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            noinline fn demo(x: u32, y: u32) -> u32 {
                var plus: u32 = +x;
                var inv: u32 = ~x;
                var rem: u32 = x % y;
                var sal: u32 = x <<< y;
                var sar: u32 = x >>> y;
                var rol: u32 = x <%< y;
                var ror: u32 = x >%> y;
                return plus + inv + rem + sal + sar + rol + ror;
            }

            var result: u32 = demo(8, 1);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Contain("NOT"));
        Assert.That(build.AssemblyText, Does.Contain("GETQY"));
        Assert.That(build.AssemblyText, Does.Contain("SHL"));
        Assert.That(build.AssemblyText, Does.Contain("SAR"));
        Assert.That(build.AssemblyText, Does.Contain("ROL"));
        Assert.That(build.AssemblyText, Does.Contain("ROR"));
    }

    [Test]
    public void AddressOfLocal_EmitsSymbolAddressForSyntheticStorage()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            noinline fn demo(param: u32) -> u32 {
                var x: u32 = param;
                var p: *reg u32 = &x;
                var sink: u32 = 0;
                asm volatile {
                    MOV {sink}, {p}
                };
                return sink;
            }

            var result: u32 = demo(1);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(MirTextWriter.Write(build.MirModule), Does.Match(@"load @g_x_\d+"));
        Assert.That(build.AssemblyText, Does.Match(@"MOV _r\d+,\s+g_x_\d+"));
    }

    [Test]
    public void AddressOfParameter_EmitsSyntheticStorage()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            noinline fn demo(param: u32) -> u32 {
                var p: *reg u32 = &param;
                var sink: u32 = 0;
                asm volatile {
                    MOV {sink}, {p}
                };
                return sink;
            }

            var result: u32 = demo(1);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(MirTextWriter.Write(build.MirModule), Does.Match(@"load @g_param_\d+"));
        Assert.That(build.AssemblyText, Does.Match(@"MOV g_param_\d+,\s+_r1"));
    }

    [Test]
    public void VolatilePointerDeref_RemainsInMirAsSideEffect()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            noinline fn demo(p: *reg volatile u32) -> u32 {
                p.*;
                return 0;
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = true,
            EnableLirOptimizations = false,
        });

        string mir = MirTextWriter.Write(build.MirModule);
        Assert.That(mir, Does.Contain("load.deref.reg"));
        Assert.That(mir, Does.Contain("sidefx"));
    }

    [Test]
    public void VolatileMultiPointerIndex_RemainsInMirAsSideEffect()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            noinline fn demo(p: [*]reg volatile u32, i: u32) -> u32 {
                p[i];
                return 0;
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = true,
            EnableLirOptimizations = false,
        });

        string mir = MirTextWriter.Write(build.MirModule);
        Assert.That(mir, Does.Contain("load.index.reg"));
        Assert.That(mir, Does.Contain("sidefx"));
    }

    [Test]
    public void ArrayLiteral_LowersExplicitElementsToIndexedStores()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var values: [3]u32 = [1, 2, 3];
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        string mir = MirTextWriter.Write(build.MirModule);
        Assert.That(mir, Does.Contain("%v0:[3]u32 = const null"));
        Assert.That(Regex.Matches(mir, @"store index\.reg\(").Count, Is.EqualTo(3), mir);
        Assert.That(mir, Does.Contain("store.place g_values"));
    }

    [Test]
    public void ArrayLiteral_SpreadFillsRemainingSlotsInMir()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var values: [4]u32 = [1, 2...];
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        string mir = MirTextWriter.Write(build.MirModule);
        Assert.That(Regex.Matches(mir, @"store index\.reg\(").Count, Is.EqualTo(4), mir);
        Assert.That(mir, Does.Contain("const 2"));
        Assert.That(mir, Does.Contain("const 3"));
    }

    [Test]
    public void EmptyArrayLiteral_FillsEachSlotInMir()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var values: [2]u32 = [];
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        string mir = MirTextWriter.Write(build.MirModule);
        Assert.That(Regex.Matches(mir, @"store index\.reg\(").Count, Is.EqualTo(2), mir);
        Assert.That(Regex.Matches(mir, @"const 0").Count, Is.GreaterThanOrEqualTo(2), mir);
    }

    [Test]
    public void CompilerDriver_ArrayLiteralReportsUnsupportedLowering()
    {
        CompilationResult compilation = CompilerDriver.Compile("""
            reg var values: [2]u32 = [1, 2];
            """, "array_literal.blade");

        Assert.That(compilation.IrBuildResult, Is.Not.Null);
        Assert.That(compilation.Diagnostics.Any(d => d.Code == DiagnosticCode.E0401_UnsupportedLowering), Is.True);
        Assert.That(compilation.Diagnostics.Any(d => d.Message.Contains("'store.index.reg'", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void ArrayLiteral_SpreadWithExactContextLengthDoesNotAddExtraStores()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var values: [2]u32 = [1, 2...];
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        string mir = MirTextWriter.Write(build.MirModule);
        Assert.That(Regex.Matches(mir, @"store index\.reg\(").Count, Is.EqualTo(2), mir);
    }

    [Test]
    public void EnumLiteral_GlobalInitializer_LowersToImmediateBackingValue()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type Mode = enum (u8) {
                Off = 0,
                On = 1,
                ...,
            };

            reg var mode: Mode = .On;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Match(@"MOV g_mode, #1"));
    }

    [Test]
    public void BitfieldAssignment_LowersToInsertMirOpcode()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type Flags = bitfield (u32) {
                pad0: nib,
                high: nib,
            };

            reg var flags: Flags = undefined;
            flags.high = 3;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        string mir = MirTextWriter.Write(build.MirModule);
        Assert.That(mir, Does.Contain("bitfield.insert.4.4"));
    }

    [Test]
    public void BitfieldAlignedReads_SelectSpecializedP2Instructions()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type Flags = bitfield (u32) {
                pad0: nib,
                low: nib,
                bytev: u8,
                flag: bool,
            };

            noinline fn demo(raw: u32) -> u32 {
                var flags: Flags = bitcast(Flags, raw);
                var low: nib = flags.low;
                var bytev: u8 = flags.bytev;
                var flag: bool = flags.flag;
                return (low as u32) + (bytev as u32);
            }

            reg var outv: u32 = demo(0);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Contain("GETNIB"));
        Assert.That(build.AssemblyText, Does.Contain("GETBYTE"));
        Assert.That(build.AssemblyText, Does.Contain("TESTB"));
    }

    [Test]
    public void ModuloCompoundAssignment_UsesUpdatePlaceLowering()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var acc: u32 = 17;
            acc %= 3;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(MirTextWriter.Write(build.MirModule), Does.Contain("update.place g_acc Modulo"));
        Assert.That(build.AssemblyText, Does.Contain("GETQY"));
    }

    [Test]
    public void FinalAssemblyWriter_FormatsInlineAsmAndRegisterFileForReadability()
    {
        AsmModule module = new([],
        [
            new AsmFunction("$top", isEntryPoint: true, CallingConventionTier.EntryPoint,
            [
                new AsmLabelNode("$top_bb0"),
                new AsmCommentNode("inline asm raw fallback begin"),
                new AsmInlineTextNode("            "),
                new AsmInlineTextNode("                    MOV _r4, #target_label"),
                new AsmInlineTextNode("                "),
                new AsmCommentNode("inline asm raw fallback end"),
                new AsmInstructionNode("MOV", [new AsmSymbolOperand("_r4"), new AsmImmediateOperand(0)]),
                new AsmCommentNode("--- register file ---"),
                new AsmLabelNode("g_input_word_7"),
                new AsmDirectiveNode("LONG 13"),
                new AsmLabelNode("g_dead_code_visible_10"),
                new AsmDirectiveNode("LONG 0"),
                new AsmLabelNode("_r4"),
                new AsmDirectiveNode("LONG 0"),
            ]),
        ]);

        string assembly = FinalAssemblyWriter.Write(module);

        Assert.That(assembly, Does.Contain("""
              l_top
              l_top_bb0
                ' inline asm raw fallback begin

                MOV _r4, #target_label

                ' inline asm raw fallback end
                MOV _r4, #0

            ' --- register file ---
            """));
        Assert.That(assembly, Does.Contain("g_input_word_7         LONG 13"));
        Assert.That(assembly, Does.Contain("g_dead_code_visible_10 LONG  0"));
        Assert.That(assembly, Does.Contain("_r4                    LONG  0"));
        Assert.That(assembly, Does.Not.Contain("            MOV _r4, #target_label"));
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
    public void NoinlineFunction_IsNeverInlinedAtSingleCallsite()
    {
        const string source = """
            noinline fn helper(x: u32) -> u32 {
                return x + 1;
            }

            fn apply(v: u32) -> u32 {
                return helper(v);
            }
            """;

        (BoundProgram program, DiagnosticBag diagnostics) = Bind(source);
        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableSingleCallsiteInlining = true,
            EnableMirOptimizations = false,
        });

        string mir = MirTextWriter.Write(build.MirModule);
        Assert.That(mir, Does.Contain("call helper("));
    }

    [Test]
    public void InlineAsm_TypedMode_SupportsColonTerminatedLabels()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn f(v: u32) -> u32 {
                var out: u32 = 0;
                asm {
                    IF_Z JMP #done
                    MOV {out}, {v}
                    done:
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
        Assert.That(function.Nodes.OfType<AsmInlineTextNode>(), Is.Empty);
        Assert.That(build.AssemblyText, Does.Not.Contain("done:"));
        Assert.That(build.AssemblyText, Does.Not.Contain("IF_Z JMP #done"));
        Assert.That(build.AssemblyText, Does.Match(@"IF_Z JMP #__asm_\d+_\d+_done"));
        Assert.That(Regex.IsMatch(build.AssemblyText, @"^\s*__asm_\d+_\d+_done\s*$", RegexOptions.Multiline), Is.True, build.AssemblyText);
    }

    [Test]
    public void InlineAsm_Volatile_LocalLabelsArePrefixedAndEmitWithoutColon()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn f(v: u32) -> u32 {
                var out: u32 = 0;
                asm volatile {
                    IF_Z JMP #done
                    MOV {out}, {v}
                    done:
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

        Assert.That(build.AssemblyText, Does.Not.Contain("done:"));
        Assert.That(build.AssemblyText, Does.Not.Contain("IF_Z JMP #done"));
        Assert.That(build.AssemblyText, Does.Match(@"IF_Z JMP #__asm_\d+_\d+_done"));
        Assert.That(Regex.IsMatch(build.AssemblyText, @"^\s*__asm_\d+_\d+_done\s*$", RegexOptions.Multiline), Is.True, build.AssemblyText);
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

        Assert.That(build.AssemblyText, Does.Match(@"MOV\s+_r\d+,\s+#0"));
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
                    MOV {out}, #target_label + 4 // keep raw fallback comment
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
        Assert.That(build.AssemblyText, Does.Contain("#target_label + 4"));
        Assert.That(build.AssemblyText, Does.Contain("' keep raw fallback comment"));
    }

    [Test]
    public void InlineAsm_FlagOutput_StaysOpaqueForOptimizationSafety()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn test_bit(val: u32, pos: u32) -> bool@C {
                asm {
                    TESTB {val}, {pos} WC
                } -> result: bool@C;
                return result;
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

        Assert.That(build.AssemblyText, Does.Match(@"MOV\s+g_flags,\s+_r\d+"));
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
        Assert.That(build.AssemblyText, Does.Not.Match(@"LONG\s+0\b"));
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
        Assert.That(build.AssemblyText, Does.Not.Match(@"LONG\s+0\b"));
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

        Assert.That(build.AssemblyText, Does.Match(@"\bg_g\b"));
        Assert.That(build.AssemblyText, Does.Match(@"LONG\s+1000\b"));
        Assert.That(build.AssemblyText, Does.Not.Contain("MOV g_g,"));
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
        int longZeroCount = Regex.Matches(build.AssemblyText, @"LONG\s+0\b").Count;

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

    [Test]
    public void AdvancedSemantics_CompilerDriverReportsUnsupportedLowerings()
    {
        CompilationResult compilation = CompilerDriver.Compile("""
            const Pair = packed struct { left: u32, right: u32 };

            coro fn worker(seed: u32) -> u32 {
                var pair: Pair = .{ .left = seed, .right = seed };
                var arr: [2]u32 = undefined;
                var ptr: *u32 = undefined;
                var sink: u32 = 0;

                while (true) { break; }
                loop { continue; }
                rep loop (2) { sink = sink + 1; }
                rep for (1..2) -> i { sink = sink + i; }
                noirq { sink = sink + 1; }

                sink = pair.left;
                pair.right = sink;
                sink = arr[0];
                arr[1] = sink;
                sink = ptr.*;
                ptr.* = sink;
                sink = if (true) pair.left else pair.right;
                @encod(sink);
                1..2;
                asm {
                    MOV {sink}, {sink}
                };
                yieldto worker(seed);
                return sink;
            }

            int1 fn irq() void {
                yield;
            }

            yieldto worker(1);
            """, "advanced_semantics.blade");

        Assert.That(compilation.IrBuildResult, Is.Not.Null);
        Assert.That(compilation.Diagnostics.Any(d => d.Code == DiagnosticCode.E0401_UnsupportedLowering), Is.True);
        Assert.That(compilation.Diagnostics.Any(d => d.Message.Contains("'yieldto'", StringComparison.Ordinal)), Is.True);
        Assert.That(compilation.Diagnostics.Any(d => d.Message.Contains("'yield'", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void BitfieldAlignedSignedExtracts_EmitGetByteGetWordAndSignExtension()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type SignedFields = bitfield (u32) {
                low: nib,
                high: nib,
                bytev: i8,
                wordv: i16,
            };

            noinline fn demo(raw: u32) -> i32 {
                var fields: SignedFields = bitcast(SignedFields, raw);
                var a: i8 = fields.bytev;
                var b: i16 = fields.wordv;
                return (a as i32) + (b as i32);
            }

            var result: i32 = demo(0x80FF_0000);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Contain("GETBYTE"));
        Assert.That(build.AssemblyText, Does.Contain("GETWORD"));
        Assert.That(build.AssemblyText, Does.Contain("SIGNX"));
    }

    [Test]
    public void BitfieldUnalignedExtract_FallsBackToShiftAndMask()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type UnalignedFields = bitfield (u32) {
                flag: bool,
                nibble: nib,
            };

            noinline fn demo(raw: u32) -> nib {
                var fields: UnalignedFields = bitcast(UnalignedFields, raw);
                return fields.nibble;
            }

            var result: nib = demo(0xFFFF_FFFF);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Contain("SHR"));
        Assert.That(build.AssemblyText, Does.Contain("ZEROX"));
    }

    [Test]
    public void BitfieldUnalignedSignedExtract_UsesShiftAndSignExtension()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type UnalignedFields = bitfield (u32) {
                flag: bool,
                bytev: i8,
            };

            noinline fn demo(raw: u32) -> i8 {
                var fields: UnalignedFields = bitcast(UnalignedFields, raw);
                return fields.bytev;
            }

            var result: i8 = demo(0xFFFF_FFFF);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Contain("SHR"));
        Assert.That(build.AssemblyText, Does.Contain("SIGNX"));
    }

    [Test]
    public void BitfieldWholeWidthAndUnalignedInsert_CoverSpecialAndFallbackPaths()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type WholeValue = bitfield (u32) {
                all: u32,
            };

            type UnalignedFields = bitfield (u32) {
                flag: bool,
                nibble: nib,
            };

            noinline fn demo(raw: u32, value: nib) -> u32 {
                var whole: WholeValue = bitcast(WholeValue, raw);
                var unaligned: UnalignedFields = bitcast(UnalignedFields, raw);
                whole.all = raw;
                unaligned.nibble = value;
                return bitcast(u32, whole);
            }

            var result: u32 = demo(1, 2);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Contain("unhandled aligned fallback for bitfield.insert.1.4"));
        Assert.That(build.AssemblyText, Does.Match(@"MOV _r\d+,\s+_r\d+"));
    }

    [Test]
    public void BitfieldAlignedWordInsert_UsesSetWord()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type WordFields = bitfield (u32) {
                low: u16,
                high: u16,
            };

            noinline fn demo(raw: u32, value: u16) -> u32 {
                var fields: WordFields = bitcast(WordFields, raw);
                fields.high = value;
                return bitcast(u32, fields);
            }

            var result: u32 = demo(1, 2);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Contain("SETWORD"));
    }

    [Test]
    public void VolatileMultiPointerIndexRead_RemainsSideEffectfulInMir()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var values: [4]u32 = undefined;

            noinline fn demo(many: [*]reg volatile u32) -> u32 {
                many[1];
                return 0;
            }

            var sink: u32 = demo(&values);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = true,
            EnableLirOptimizations = false,
        });

        string mir = MirTextWriter.Write(build.MirModule);
        Assert.That(mir, Does.Contain("load.index.reg"));
        Assert.That(mir, Does.Contain("; sidefx"));
    }

    [Test]
    public void CompoundAssignments_CoverNonVolatileIndexAndVolatileDerefReadPaths()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var base: u32 = 7;
            reg var values: [4]u32 = undefined;

            noinline fn demo(ptr: *reg volatile u32, many: [*]reg u32) void {
                many[0] += 1;
                ptr.* += 1;
            }

            demo(&base, &values);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        MirModule mirModule = MirLowerer.Lower(program);
        string mir = MirTextWriter.Write(mirModule);

        Assert.That(mir, Does.Contain("load.index.reg"));
        Assert.That(mir, Does.Contain("load.deref.reg"));
        Assert.That(mir, Does.Contain("; sidefx"));
    }

    [Test]
    public void CompoundAssignments_ExerciseMirAssignmentTargetReadPaths()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type Pair = packed struct {
                value: u32,
            };

            type Flags = bitfield (u32) {
                low: nib,
                high: nib,
            };

            noinline fn demo(ptr: *reg u32, many: [*]reg volatile u32) void {
                var pair: Pair = .{ .value = 1 };
                var flags: Flags = undefined;
                pair.value += 1;
                many[0] += 1;
                ptr.* += 1;
                flags.high += 1;
            }

            reg var base: u32 = 7;
            reg var values: [4]u32 = undefined;
            demo(&base, &values);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        MirModule mirModule = MirLowerer.Lower(program);
        string mir = MirTextWriter.Write(mirModule);

        Assert.That(mir, Does.Contain("load.member.value"));
        Assert.That(mir, Does.Contain("load.index.reg"));
        Assert.That(mir, Does.Contain("load.deref.reg"));
        Assert.That(mir, Does.Contain("bitfield.extract.4.4"));
        Assert.That(mir, Does.Contain("bitfield.insert.4.4"));
    }

    [Test]
    public void ErrorAssignmentTarget_LowersToStoreError()
    {
        TextSpan span = new(0, 0);
        BoundAssignmentStatement statement = new(
            new BoundErrorAssignmentTarget(span),
            new BoundLiteralExpression(1, span, BuiltinTypes.U32),
            TokenKind.PlusEqual,
            span);
        BoundProgram program = new(
            [statement],
            [],
            [],
            new Dictionary<string, TypeSymbol>(),
            new Dictionary<string, FunctionSymbol>(),
            new Dictionary<string, ImportedModule>());

        MirModule mirModule = MirLowerer.Lower(program);
        string mir = MirTextWriter.Write(mirModule);

        Assert.That(mir, Does.Contain("store.error"));
    }

    [Test]
    public void AsmLowerer_InvalidBitfieldOpcodes_EmitComments()
    {
        TextSpan span = new(0, 0);
        LirVirtualRegister sourceRegister = new(0);
        LirVirtualRegister valueRegister = new(1);
        LirVirtualRegister destinationRegister = new(2);
        LirFunction function = new(
            "demo",
            isEntryPoint: true,
            FunctionKind.Default,
            [],
            [
                new LirBlock(
                    "bb0",
                    [],
                    [
                        new LirOpInstruction(
                            "bitfield.extract.bad",
                            destinationRegister,
                            BuiltinTypes.U32,
                            [new LirRegisterOperand(sourceRegister)],
                            hasSideEffects: false,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            span),
                        new LirOpInstruction(
                            "bitfield.insert.bad",
                            destinationRegister,
                            BuiltinTypes.U32,
                            [new LirRegisterOperand(sourceRegister), new LirRegisterOperand(valueRegister)],
                            hasSideEffects: false,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            span),
                    ],
                    new LirReturnTerminator([], span)),
            ]);

        AsmModule asmModule = AsmLowerer.Lower(new LirModule([function]));
        string asmir = AsmTextWriter.Write(asmModule);

        Assert.That(asmir, Does.Contain("invalid bitfield.extract.bad"));
        Assert.That(asmir, Does.Contain("invalid bitfield.insert.bad"));
    }

    [Test]
    public void AsmLowerer_BitfieldExtractWithoutResultType_SkipsExtension()
    {
        TextSpan span = new(0, 0);
        LirVirtualRegister sourceRegister = new(0);
        LirVirtualRegister destinationRegister = new(1);
        LirFunction function = new(
            "demo",
            isEntryPoint: true,
            FunctionKind.Default,
            [],
            [
                new LirBlock(
                    "bb0",
                    [],
                    [
                        new LirOpInstruction(
                            "bitfield.extract.8.8",
                            destinationRegister,
                            resultType: null,
                            [new LirRegisterOperand(sourceRegister)],
                            hasSideEffects: false,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            span),
                        new LirOpInstruction(
                            "bitfield.extract.16.16",
                            destinationRegister,
                            resultType: null,
                            [new LirRegisterOperand(sourceRegister)],
                            hasSideEffects: false,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            span),
                        new LirOpInstruction(
                            "bitfield.extract.1.4",
                            destinationRegister,
                            resultType: null,
                            [new LirRegisterOperand(sourceRegister)],
                            hasSideEffects: false,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            span),
                    ],
                    new LirReturnTerminator([], span)),
            ]);

        AsmModule asmModule = AsmLowerer.Lower(new LirModule([function]));
        string asmir = AsmTextWriter.Write(asmModule);

        Assert.That(asmir, Does.Contain("GETBYTE"));
        Assert.That(asmir, Does.Contain("GETWORD"));
        Assert.That(asmir, Does.Contain("SHR"));
    }

    private static IEnumerable<string> AcceptProgramsForPipeline()
    {
        string[] files =
        [
            "empty.blade",
            "expressions.blade",
            "intrinsics.blade",
            "types.blade",
            "variable_declarations.blade",
            Path.Combine("InlineAsm", "basic_instructions.blade"),
            Path.Combine("InlineAsm", "condition_prefixes.blade"),
            Path.Combine("InlineAsm", "flag_output.blade"),
            Path.Combine("InlineAsm", "volatile_basic.blade"),
        ];

        foreach (string file in files)
            yield return Path.Combine("Accept", file);
    }

    [TestCaseSource(nameof(AcceptProgramsForPipeline))]
    public void AcceptProgram_CanRunFullIrPipeline(string path)
    {
        string repoTestsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Blade.Tests"));
        string fullPath = Path.Combine(repoTestsRoot, path);
        string source = File.ReadAllText(fullPath);
        CompilationResult compilation = CompilerDriver.Compile(source, fullPath);

        Assert.That(compilation.Diagnostics, Is.Empty, path + Environment.NewLine + string.Join(Environment.NewLine, compilation.Diagnostics));
        Assert.That(compilation.IrBuildResult, Is.Not.Null);
        Assert.That(compilation.IrBuildResult!.AssemblyText, Does.StartWith("DAT"));
    }

    private static IEnumerable<string> AcceptProgramsWithUnsupportedLowerings()
    {
        string[] files =
        [
            "advanced_semantics.blade",
            "control_flow.blade",
            "function_declarations.blade",
            "struct_types.blade",
        ];

        foreach (string file in files)
            yield return Path.Combine("Accept", file);
    }

    [TestCaseSource(nameof(AcceptProgramsWithUnsupportedLowerings))]
    public void AcceptProgram_WithUnsupportedLowerings_ReportsDiagnostics(string path)
    {
        string repoTestsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Blade.Tests"));
        string fullPath = Path.Combine(repoTestsRoot, path);
        string source = File.ReadAllText(fullPath);
        CompilationResult compilation = CompilerDriver.Compile(source, fullPath);

        Assert.That(compilation.Diagnostics.Any(d => d.Code == DiagnosticCode.E0401_UnsupportedLowering), Is.True, path);
        Assert.That(compilation.IrBuildResult, Is.Not.Null, path);
    }


    private static IEnumerable<string> RepositorySamplesForPipeline()
    {
        string[] files =
        [
            Path.Combine("Examples", "blinky.blade"),
            Path.Combine("Examples", "clamp.blade"),
            Path.Combine("Examples", "fibonacci.blade"),
            Path.Combine("Examples", "inline_asm_bit_test.blade"),
            Path.Combine("Examples", "inline_asm_cordic.blade"),
            Path.Combine("Examples", "inline_asm_streamer.blade"),
            Path.Combine("Examples", "register_aliases.blade"),
            Path.Combine("Examples", "sum_loop.blade"),
            Path.Combine("Demonstrators", "Asm", "volatile_routines.blade"),
            Path.Combine("Demonstrators", "Asm", "optimizer_exercises.blade"),
            Path.Combine("Demonstrators", "Asm", "io_regular_asm.blade"),
            Path.Combine("Demonstrators", "Asm", "math_routines.blade"),
            Path.Combine("Demonstrators", "Bugs", "missing-copy.blade"),
        ];

        foreach (string file in files)
            yield return file;
    }

    [TestCaseSource(nameof(RepositorySamplesForPipeline))]
    public void RepositorySample_CanRunFullIrPipeline(string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relativePath));
        string source = File.ReadAllText(fullPath);
        CompilationResult compilation = CompilerDriver.Compile(source, fullPath);

        Assert.That(compilation.Diagnostics, Is.Empty, relativePath + Environment.NewLine + string.Join(Environment.NewLine, compilation.Diagnostics));
        Assert.That(compilation.IrBuildResult, Is.Not.Null);
        Assert.That(compilation.IrBuildResult!.AssemblyText, Does.StartWith("DAT"));
    }

    [Test]
    public void ForLoop_NonIterableWithBinding_LowersWithoutCrash()
    {
        // When the iterable is not an integer or array, the binder reports a
        // diagnostic but produces a BoundForStatement with IndexVariable = null.
        // The MIR lowerer must handle this gracefully.
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            var x: bool = true;
            for (x) -> item { }
            """);

        Assert.That(diagnostics.Count, Is.GreaterThan(0));
        Assert.DoesNotThrow(() => IrPipeline.Build(program, new IrPipelineOptions()));
    }

}
