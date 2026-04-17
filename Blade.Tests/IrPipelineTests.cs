using System.IO;
using System.Linq;
using System.Reflection;
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
using DiagnosticBag = System.Collections.Generic.IReadOnlyList<Blade.Diagnostics.Diagnostic>;

namespace Blade.Tests;

[TestFixture]
public class IrPipelineTests
{
    private static (BoundProgram Program, IReadOnlyList<Diagnostic> Diagnostics) Bind(string text)
    {
        CompilationResult result = CompilerDriver.Compile(text, filePath: "<input>", new CompilationOptions
        {
            EmitIr = false,
        });
        return (result.BoundProgram, result.Diagnostics);
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
    public void EntryPointExit_ExportsBladeEntryAndJumpsToBladeHalt()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            var flag: u32 = 1;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        IrBuildResult build = IrPipeline.Build(program);

        Assert.That(build.AssemblyText, Does.Contain("blade_entry"));
        Assert.That(build.AssemblyText, Does.Contain("JMP #blade_halt"));
        Assert.That(build.AssemblyText, Does.Contain("blade_halt"));
        Assert.That(build.AssemblyText, Does.Contain("REP #1, #0"));
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

        Assert.That(mir, Does.Contain("const 30"));
        Assert.That(mir, Does.Contain("fn pair"));
        Assert.That(lir, Does.Not.Contain("call pair"));
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

            var input: u32 = 255;
            var result: u32 = demo(input);
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

        Assert.That(diagnostics.Select(static diagnostic => diagnostic.Code), Is.EqualTo([DiagnosticCode.W0261_ComptimeIntegerTruncation]));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Match(@"g_narrowed\s+LONG\s+1"));
    }

    [Test]
    public void MultiReturnFlagValues_PropagateIntoIfExpressionsAndDiscardExtractions()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            noinline fn two_ret(a: u32, b: u32) -> u32, bool {
                return a + b, (a < b);
            }

            noinline fn three_ret(a: u32, b: u32) -> u32, bool, bool {
                return a + b, (a < b), (a == b);
            }

            var sum2: u32 = undefined;
            var lt2: bool = undefined;
            sum2, lt2 = two_ret(3, 4);
            _, _ = two_ret(4, 3);

            var sum3: u32 = undefined;
            var lt3: bool = undefined;
            var eq3: bool = undefined;
            sum3, lt3, eq3 = three_ret(5, 5);

            var lt2_u: u32 = if (lt2) 1 else 0;
            var eq3_u: u32 = if (eq3) 1 else 0;
            reg var packed: u32 = 0;
            packed = (sum2 & 0xFFFF) | (lt2_u << 16) | (eq3_u << 17);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program);
        string mir = MirTextWriter.Write(build.MirModule);
        string lir = LirTextWriter.Write(build.LirModule);
        string assembly = build.AssemblyText;

        Assert.That(Regex.Matches(mir, @"branch %v\d+ \[flag:C\]").Count, Is.GreaterThanOrEqualTo(1), mir);
        Assert.That(Regex.Matches(mir, @"branch %v\d+ \[flag:Z\]").Count, Is.GreaterThanOrEqualTo(1), mir);
        Assert.That(lir, Does.Contain("call.extractC"));
        Assert.That(Regex.Matches(lir, "call\\.extractC").Count, Is.EqualTo(3), lir);
        Assert.That(Regex.Matches(lir, "call\\.extractZ").Count, Is.EqualTo(1), lir);
        Assert.That(assembly, Does.Contain("BITC"));
        Assert.That(assembly, Does.Contain("BITZ"));
        Assert.That(Regex.Matches(assembly, @"MOV _r\d+, #1").Count, Is.GreaterThanOrEqualTo(2), assembly);
    }

    [Test]
    public void RecursiveCallResult_IsCopiedBeforeSpillRestore()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            rec fn factorial(n: u32) -> u32 {
                if (n < 2) {
                    return 1;
                }

                return n * factorial(n - 1);
            }

            reg var input: u32 = 6;
            reg var result: u32 = 0;

            result = factorial(input);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program);
        string assembly = build.AssemblyText;

        Match callMatch = Regex.Match(assembly, @"CALLB #f_factorial\s+MOV (_r\d+|P[AB]), PA\s+POPB PA\s+QMUL PA, \1", RegexOptions.Singleline);
        Assert.That(callMatch.Success, Is.True, assembly);
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

            var x: u32 = 8;
            var y: u32 = 1;
            var result: u32 = demo(x, y);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Contain("NOT"));
        Assert.That(build.AssemblyText, Does.Contain("GETQY"));
        Assert.That(build.AssemblyText, Does.Contain("SAL"));
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

        Assert.That(MirTextWriter.Write(build.MirModule), Does.Contain("const &x"));
        Assert.That(build.AssemblyText, Does.Contain("MOV _r1, PA"));
        Assert.That(build.AssemblyText, Does.Contain("MOV g_x, _r1"));
        Assert.That(build.AssemblyText, Does.Contain("MOV PA, #g_x"));
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

        Assert.That(MirTextWriter.Write(build.MirModule), Does.Contain("const &param"));
        Assert.That(build.AssemblyText, Does.Contain("MOV _r1, PA"));
        Assert.That(build.AssemblyText, Does.Contain("MOV g_param, _r1"));
        Assert.That(build.AssemblyText, Does.Contain("MOV PA, #g_param"));
    }

    [Test]
    public void AddressOfGlobal_ReusesExistingGlobalStoragePlace()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var base: u32 = 7;

            noinline fn demo() -> u32 {
                var p: *reg u32 = &base;
                var sink: u32 = 0;
                asm volatile {
                    MOV {sink}, {p}
                };
                return sink;
            }

            var result: u32 = demo();
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        Assert.That(MirTextWriter.Write(build.MirModule), Does.Contain("const &base"));
        Assert.That(build.AsmModule.StoragePlaces.Count(place => place.Symbol.Name == "base"), Is.EqualTo(1));
        Assert.That(build.AssemblyText, Does.Contain("MOV PA, #g_base"));
    }

    [Test]
    public void VolatilePointerDeref_RemainsInMirAsSideEffect()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            noinline fn demo(p: *reg volatile u32) -> u32 {
                _ = p.*;
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
                _ = p[i];
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
    public void VolatileRegPointerReadExpressions_EmitIndirectCogRegisterLoads()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var sink: u32 = 0;
            reg var values: [4]u32 = undefined;

            noinline fn demo(ptr: *reg volatile u32, many: [*]reg volatile u32) -> u32 {
                _ = ptr.*;
                _ = many[1];
                return 0;
            }

            reg var base: u32 = 7;
            sink = demo(&base, &values);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = true,
            EnableLirOptimizations = false,
        });

        string asmir = AsmTextWriter.Write(build.AsmModule);
        Assert.That(Regex.Matches(asmir, @"\bALTS\b [^,\r\n]+\r?\n\s*MOV [^,\r\n]+, <altered>").Count, Is.EqualTo(1), asmir);
        Assert.That(Regex.Matches(asmir, @"\bALTS\b [^,\r\n]+, [^\r\n]+\r?\n\s*MOV [^,\r\n]+, <altered>").Count, Is.EqualTo(1), asmir);
        Assert.That(Regex.Matches(asmir, @"\bADD\b [^,\r\n]+, [^\r\n]+\r?\n\s*ALTS\b").Count, Is.EqualTo(0), asmir);
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
    public void RegArrayLiteralInitialization_EmitsIndirectCogRegisterStores()
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

        string asmir = AsmTextWriter.Write(build.AsmModule);
        Assert.That(Regex.Matches(asmir, @"\bALTD\b [^,\r\n]+, [^\r\n]+\r?\n\s*MOV <altered>, [^\r\n]+").Count, Is.EqualTo(3), asmir);
        Assert.That(Regex.Matches(asmir, @"\bADD\b [^,\r\n]+, [^\r\n]+\r?\n\s*ALTD\b").Count, Is.EqualTo(0), asmir);
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
    public void CompilerDriver_ArrayLiteralEmitsIndirectRegisterStores()
    {
        CompilationResult compilation = CompilerDriver.Compile("""
            reg var values: [2]u32 = [1, 2];
            """, "array_literal.blade");

        Assert.That(compilation.IrBuildResult, Is.Not.Null);
        Assert.That(compilation.Diagnostics.Any(d => d.Code == DiagnosticCode.E0401_UnsupportedLowering), Is.False);
        Assert.That(compilation.IrBuildResult!.AssemblyText, Does.Contain("ALTD"));
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

        Assert.That(build.AssemblyText, Does.Contain("g_mode LONG 1"));
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

            var outv: u32 = demo(0);
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
    public void ExtendedCompoundAssignments_UseReferenceDefinedUpdatePlaceLowering()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var acc: u32 = 17;
            acc *= 3;
            acc /= 2;
            acc <<<= 1;
            acc >>>= 1;
            acc <%<= 4;
            acc >%>= 2;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        string mir = MirTextWriter.Write(build.MirModule);
        Assert.That(mir, Does.Contain("update.place g_acc Multiply"));
        Assert.That(mir, Does.Contain("update.place g_acc Divide"));
        Assert.That(mir, Does.Contain("update.place g_acc ArithmeticShiftLeft"));
        Assert.That(mir, Does.Contain("update.place g_acc ArithmeticShiftRight"));
        Assert.That(mir, Does.Contain("update.place g_acc RotateLeft"));
        Assert.That(mir, Does.Contain("update.place g_acc RotateRight"));
        Assert.That(build.AssemblyText, Does.Contain("QMUL"));
        Assert.That(build.AssemblyText, Does.Contain("GETQX"));
        Assert.That(build.AssemblyText, Does.Contain("QDIV"));
        Assert.That(build.AssemblyText, Does.Contain("SAL"));
        Assert.That(build.AssemblyText, Does.Contain("SAR"));
        Assert.That(build.AssemblyText, Does.Contain("ROL"));
        Assert.That(build.AssemblyText, Does.Contain("ROR"));
    }

    [Test]
    public void PointerCompoundAssignments_CarryStrideMetadataThroughUpdatePlace()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            hub var words: [8]u16 = undefined;
            reg var cursor: [*]hub u16 = undefined;
            cursor = &words;
            cursor += 2;
            cursor -= 1;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        string mir = MirTextWriter.Write(build.MirModule);
        Assert.That(mir, Does.Contain("update.place g_cursor Add[2]"));
        Assert.That(mir, Does.Contain("update.place g_cursor Subtract[2]"));
        Assert.That(build.AssemblyText, Does.Contain("SHL"));
        Assert.That(build.AssemblyText, Does.Contain("ADD"));
        Assert.That(build.AssemblyText, Does.Contain("SUB"));
    }

    [Test]
    public void RangeForLoop_DoesNotMaterializeRangeInstructions()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var sink: u32 = 0;
            for (1..<3) -> i {
                sink = sink + i;
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        string mir = MirTextWriter.Write(build.MirModule);
        string lir = LirTextWriter.Write(build.LirModule);
        string asmir = AsmTextWriter.Write(build.AsmModule);

        Assert.That(mir, Does.Not.Contain("range"));
        Assert.That(lir, Does.Not.Contain("range"));
        Assert.That(asmir, Does.Not.Contain("range"));
    }

    [Test]
    public void PointerArithmetic_NonPowerOfTwoStride_UsesMultiplyAndSignedDivision()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            hub var chunks: [4][3]u8 = undefined;
            reg var diff_sink: i32 = 0;

            noinline fn diff_chunks(base: [*]hub [3]u8, step: i8) -> i32 {
                var p: [*]hub [3]u8 = base + step;
                return p - base;
            }

            diff_sink = diff_chunks(&chunks, 2);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        string mir = MirTextWriter.Write(build.MirModule);
        Assert.That(mir, Does.Contain("const 2"));
        Assert.That(build.AssemblyText, Does.Contain("MOV g_diff_sink, #2"));
    }

    [Test]
    public void PointerDifference_PowerOfTwoStride_UsesArithmeticShift()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            hub var words: [8]u16 = undefined;
            reg var diff_sink: i32 = 0;

            noinline fn diff_words(base: [*]hub u16, step: u8) -> i32 {
                var p: [*]hub u16 = base + step;
                return p - base;
            }

            diff_sink = diff_words(&words, 2);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        string mir = MirTextWriter.Write(build.MirModule);
        Assert.That(mir, Does.Contain("const 2"));
        Assert.That(build.AssemblyText, Does.Contain("MOV g_diff_sink, #2"));
    }

    [Test]
    public void FinalAssemblyWriter_FormatsInlineAsmAndRegisterFileForReadability()
    {
        StoragePlace r4 = CreateStoragePlace("r4", emittedName: "_r4");
        StoragePlace inputWord = CreateStoragePlace("input_word_7", emittedName: "g_input_word_7");
        StoragePlace deadCodeVisible = CreateStoragePlace("dead_code_visible_10", emittedName: "g_dead_code_visible_10");
        AsmModule module = new(
            [inputWord, deadCodeVisible, r4],
            [
                new AsmDataBlock(
                    AsmDataBlockKind.Register,
                    [
                        new AsmAllocatedStorageDefinition(inputWord, VariableStorageClass.Reg, BuiltinTypes.U32, [new AsmImmediateOperand(13L)]),
                        new AsmAllocatedStorageDefinition(deadCodeVisible, VariableStorageClass.Reg, BuiltinTypes.U32, [new AsmImmediateOperand(0L)]),
                        new AsmAllocatedStorageDefinition(r4, VariableStorageClass.Reg, BuiltinTypes.U32, [new AsmImmediateOperand(0L)]),
                    ]),
            ],
            [
                CreateAsmFunction("$top", isEntryPoint: true, CallingConventionTier.EntryPoint,
                [
                    new AsmLabelNode("$top_bb0"),
                    new AsmCommentNode("inline asm typed begin"),
                    new AsmInstructionNode(P2Mnemonic.MOV, [new AsmSymbolOperand(r4, AsmSymbolAddressingMode.Register), new AsmImmediateOperand(42)]),
                    new AsmCommentNode("inline asm typed end"),
                    new AsmInstructionNode(P2Mnemonic.MOV, [new AsmSymbolOperand(r4, AsmSymbolAddressingMode.Register), new AsmImmediateOperand(0)]),
                ]),
            ]);

        string assembly = FinalAssemblyWriter.Write(module);

        Assert.That(assembly, Does.Contain("' inline asm typed begin"));
        Assert.That(assembly, Does.Contain("MOV _r4, #42"));
        Assert.That(assembly, Does.Contain("' inline asm typed end"));
        Assert.That(assembly, Does.Contain("MOV _r4, #0"));
        Assert.That(assembly, Does.Contain("g_input_word_7         LONG 13"));
        Assert.That(assembly, Does.Contain("g_dead_code_visible_10 LONG 0"));
        Assert.That(assembly, Does.Contain("_r4                    LONG 0"));
    }

    [Test]
    public void FinalAssemblyWriter_PrefixesFunctionSymbolsDeterministically()
    {
        AsmFunction step = CreateAsmFunction("step", isEntryPoint: false, CallingConventionTier.General,
            [
                new AsmLabelNode("step_bb0"),
                new AsmInstructionNode(P2Mnemonic.RET, []),
            ]);
        AsmModule module = new(
            [],
            [],
            [
                step,
                CreateAsmFunction("caller", isEntryPoint: true, CallingConventionTier.EntryPoint,
                [
                    new AsmLabelNode("caller_bb0"),
                    new AsmInstructionNode(P2Mnemonic.CALLB, [new AsmSymbolOperand(step, AsmSymbolAddressingMode.Immediate)]),
                    new AsmInstructionNode(P2Mnemonic.MOV, [new AsmPhysicalRegisterOperand(new P2Register(0)), new AsmSymbolOperand(step, AsmSymbolAddressingMode.Register)]),
                ]),
            ]);

        string assembly = FinalAssemblyWriter.Write(module);

        Assert.That(assembly, Does.Contain("  f_step"));
        Assert.That(assembly, Does.Contain("    CALLB #f_step"));
        Assert.That(assembly, Does.Contain("    MOV r0, f_step"));
        Assert.That(assembly, Does.Contain("  f_caller"));
        Assert.That(assembly, Does.Not.Contain("  step\n"));
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

            var sink: u32 = f(1);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableSingleCallsiteInlining = false,
            EnableMirInlining = false,
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        _ = build.AsmModule.Functions.Single(f => f.Name == "f");
        Assert.That(build.AssemblyText, Does.Not.Contain("done:"));
        Assert.That(build.AssemblyText, Does.Not.Contain("IF_Z JMP #done"));
        Assert.That(build.AssemblyText, Does.Contain("__asm_"));
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

            var sink: u32 = f(1);
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

            var flags: u32 = test_and_set_bit(0, 5);
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
    public void InlineAsm_TemporaryRegisters_LowerThroughSharedBindingPipeline()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn f() -> u32 {
                var out: u32 = 0;
                asm {
                    MOV %0, #10
                    ADD %0, #20
                    MOV %1, %0
                    MOV {out}, %1
                };
                return out;
            }

            var sink: u32 = f();
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableSingleCallsiteInlining = false,
            EnableMirInlining = false,
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        string lir = LirTextWriter.Write(build.LirModule);
        Match tempMatch = Regex.Match(lir, @"inlineasm out=%r\d+:w, %0=(%r\d+):rw, %1=(%r\d+):rw");
        Assert.That(tempMatch.Success, Is.True, lir);
        Assert.That(tempMatch.Groups[1].Value, Is.Not.EqualTo(tempMatch.Groups[2].Value), lir);
        Assert.That(build.AssemblyText, Does.Not.Contain("%0"));
        Assert.That(build.AssemblyText, Does.Not.Contain("%1"));
    }

    [Test]
    public void AsmFunction_TemporaryRegisters_AreSupported()
    {
        CompilationResult compilation = CompilerDriver.Compile("""
            asm fn add_vals() -> u32 {
                MOV %0, #10
                ADD %0, #20
                MOV {return}, %0
            }

            reg var sink: u32 = 0;
            sink = add_vals();
            """, "asm_fn_temps.blade");

        Assert.That(compilation.Diagnostics, Is.Empty, string.Join(Environment.NewLine, compilation.Diagnostics));
        Assert.That(compilation.IrBuildResult, Is.Not.Null);
        Assert.That(compilation.IrBuildResult!.AssemblyText, Does.Contain("ADD"));
        Assert.That(compilation.IrBuildResult.AssemblyText, Does.Not.Contain("%0"));
    }

    [Test]
    public void InlineAsm_TemporaryRegisterReadBeforeWrite_WarnsButStillBuilds()
    {
        CompilationResult compilation = CompilerDriver.Compile("""
            asm fn read_temp() -> u32 {
                MOV {return}, %0
            }

            reg var sink: u32 = 0;
            sink = read_temp();
            """, "asm_temp_warning.blade");

        Assert.That(compilation.Diagnostics.Any(static diagnostic => diagnostic.Code == DiagnosticCode.W0307_InlineAsmTempReadBeforeWrite), Is.True);
        Assert.That(compilation.Diagnostics.Any(static diagnostic => diagnostic.IsError), Is.False, string.Join(Environment.NewLine, compilation.Diagnostics));
        Assert.That(compilation.IrBuildResult, Is.Not.Null);
        Assert.That(MirTextWriter.Write(compilation.IrBuildResult!.MirModule), Does.Not.Contain("const 0:u32"));
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

            var sink: u32 = f(1);
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
        Assert.That(function.Nodes.OfType<AsmVolatileRegionBeginNode>().Any(), Is.True);
        Assert.That(function.Nodes.OfType<AsmInstructionNode>().Any(n => n.IsNonElidable), Is.True);
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

            var sink: u32 = f(1);
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

            var sink: u32 = f(1);
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
        string asmir = AsmTextWriter.Write(build.AsmModule);
        Assert.That(asmir, Does.Contain("CALLPB"));
        Assert.That(asmir, Does.Contain("ADD _r0, #1"));
    }

    [Test]
    public void GeneralTierFunctionWithoutParameters_GetsSharedRegisterReturnPlace()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var source: u32 = 1;
            reg var sink: u32 = 0;

            noinline fn leaf_source() -> u32 {
                return source;
            }

            noinline fn middle() -> u32 {
                return leaf_source();
            }

            noinline fn answer() -> u32 {
                return middle();
            }

            sink = answer();
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableSingleCallsiteInlining = false,
            EnableMirInlining = false,
            EnableMirOptimizations = false,
            EnableLirOptimizations = false,
        });

        AsmFunction function = build.AsmModule.Functions.Single(f => f.Name == "answer");
        StoragePlace returnPlace = build.AsmModule.StoragePlaces.Single(place => place.Symbol.Name == "gen_answer_ret0");

        Assert.That(function.CcTier, Is.EqualTo(CallingConventionTier.General));
        Assert.That(returnPlace.RegisterRole, Is.EqualTo(StoragePlaceRegisterRole.InternalShared));
        Assert.That(returnPlace.IsInternalRegisterSlot, Is.True);
        Assert.That(returnPlace.Symbol.StorageClass, Is.EqualTo(VariableStorageClass.Reg));
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

            var sink: u32 = f(1);
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
        Assert.That(build.AssemblyText, Does.Contain("MOV _r0, _r0"));
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

            var sink: u32 = f(1);
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
        Assert.That(build.AssemblyText, Does.Not.Match(@"MOV\s+(_r\d+),\s+\1\b"), build.AssemblyText);
    }

    [Test]
    public void InlineAsm_NonVolatile_RejectsUnsupportedOperandShape()
    {
        (BoundProgram _, DiagnosticBag diagnostics) = Bind("""
            fn f(x: u32) -> u32 {
                var out: u32 = 0;
                asm {
                    MOV {out}, #target_label + 4
                };
                return out;
            }

            var sink: u32 = f(1);
            """);

        Assert.That(diagnostics.Count, Is.GreaterThan(0));
        Assert.That(diagnostics.Any(d => d.Code.ToString().StartsWith("E030")), Is.True);
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

            var sink: bool = test_bit(0, 1);
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
        Assert.That(function.Nodes.OfType<AsmInstructionNode>().Any(n => n.Mnemonic == P2Mnemonic.TESTB), Is.True);
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
            .SelectMany(f => f.Blocks.Select(static block => block.Ref))
            .GroupBy(static blockRef => blockRef)
            .Where(group => group.Count() > 1)
            .Select(_ => "duplicate-ref")
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
        Assert.That(place.Placement, Is.EqualTo(StoragePlacePlacement.FixedAlias));
        Assert.That(place.FixedAddress, Is.EqualTo(0x1FC));
        Assert.That(build.AssemblyText, Does.Contain("OR OUTA, #16"));
        Assert.That(build.AssemblyText, Does.Not.Contain("OUTA = $1FC"));
        Assert.That(build.AssemblyText, Does.Not.Match(@"LONG\s+0\b"));
        Assert.That(build.AssemblyText, Does.Not.Contain("g_OUTA"));
    }

    [Test]
    public void PlainGlobalNamedLikeSpecialRegister_RemainsNormalStorage()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var OUTA: u32 = 0;
            OUTA |= 0x10;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
        });

        StoragePlace place = build.AsmModule.StoragePlaces.Single(p => p.Symbol.Name == "OUTA");
        Assert.That(place.Placement, Is.EqualTo(StoragePlacePlacement.Allocatable));
        Assert.That(place.SpecialRegisterAlias.HasValue, Is.False);
        Assert.That(build.AssemblyText, Does.Contain("OR g_OUTA, #16"));
        Assert.That(build.AssemblyText, Does.Not.Contain("OR OUTA, #16"));
        Assert.That(build.AssemblyText, Does.Not.Contain("OUTA = $1FC"));
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
        Assert.That(build.AssemblyText, Does.Contain("LED_PORT = $1FC"));
        Assert.That(build.AssemblyText, Does.Contain("OR LED_PORT, #16"));
    }

    [Test]
    public void FoldedFixedRegisterAliases_EmitFlexspinCompatibleConConstants()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            extern reg var A0: u32 @(1);
            extern reg var A1: u32 @((+3) as u8);
            extern reg var A2: u32 @((~0) as u8);
            extern reg var A3: u32 @((-1) as u8);
            extern reg var A4: u32 @(bitcast(u8, 5 as u8));
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Contain("A0 = $1"));
        Assert.That(build.AssemblyText, Does.Contain("A1 = $3"));
        Assert.That(build.AssemblyText, Does.Contain("A2 = $FF"));
        Assert.That(build.AssemblyText, Does.Contain("A3 = $FF"));
        Assert.That(build.AssemblyText, Does.Contain("A4 = $5"));
        Assert.That(build.AssemblyText, Does.Not.Contain("0x"));
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

            noinline fn use_value(x: u32) -> u32 {
                return double(x);
            }

            var input: u32 = 42;
            var result: u32 = use_value(input);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program, new IrPipelineOptions
        {
            EnableMirOptimizations = false,
        });

        Assert.That(build.AssemblyText, Does.Not.Contain("%r"),
            "No virtual registers should remain in final output");
        Assert.That(build.AssemblyText, Does.Contain("CALLPA #42, #f_use_value"));
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
    public void AdvancedSemantics_CompilerDriverBuildsIrWithoutRangeUnsupportedLowerings()
    {
        CompilationResult compilation = CompilerDriver.Compile("""
            type Pair = struct { left: u32, right: u32 };

            coro fn worker(seed: u32) -> u32 {
                var pair: Pair = Pair { .left = seed, .right = seed };
                var arr: [2]u32 = undefined;
                var ptr: *reg u32 = undefined;
                var sink: u32 = 0;

                while (true) { break; }
                loop { continue; }
                rep for (2) { sink = sink + 1; }
                rep for (1..<2) -> i { sink = sink + i; }
                noirq { sink = sink + 1; }

                sink = pair.left;
                pair.right = sink;
                sink = arr[0];
                arr[1] = sink;
                sink = ptr.*;
                ptr.* = sink;
                sink = if (true) pair.left else pair.right;
                @encod(sink);
                for (1..<2) -> i { sink = sink + i; }
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
        Assert.That(compilation.Diagnostics.Count(d => d.Code == DiagnosticCode.E0401_UnsupportedLowering), Is.EqualTo(0));
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
        Assert.That(build.AssemblyText, Does.Not.Match(@"MOV\s+(_r\d+),\s+\1\b"), build.AssemblyText);
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
                _ = many[1];
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
            type Pair = struct {
                value: u32,
            };

            type Flags = bitfield (u32) {
                low: nib,
                high: nib,
            };

            noinline fn demo(ptr: *reg u32, many: [*]reg volatile u32) void {
                var pair: Pair = Pair { .value = 1 };
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
    public void ComparisonBranches_UseCorrectMirConditionFlags()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            noinline fn compare_flags(a: u32, b: u32) -> u32 {
                var result: u32 = 0;
                if (a == b) { result |= 0x01; }
                if (a != b) { result |= 0x02; }
                if (a < b)  { result |= 0x04; }
                if (a <= b) { result |= 0x08; }
                if (a > b)  { result |= 0x10; }
                if (a >= b) { result |= 0x20; }
                return result;
            }

            reg var sink: u32 = compare_flags(1, 2);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        MirModule mirModule = MirLowerer.Lower(program);
        string mir = MirTextWriter.Write(mirModule);

        Assert.That(Regex.Matches(mir, @"\[flag:Z\]").Count, Is.EqualTo(1), mir);
        Assert.That(Regex.Matches(mir, @"\[flag:NZ\]").Count, Is.EqualTo(1), mir);
        Assert.That(Regex.Matches(mir, @"\[flag:C\]").Count, Is.EqualTo(2), mir);
        Assert.That(Regex.Matches(mir, @"\[flag:NC\]").Count, Is.EqualTo(2), mir);
    }

    [Test]
    public void SignedOrderingComparisons_LowerToTestBAndCmps()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            noinline fn compare_signed(value: u32, other: i32) -> u32 {
                var signed: i32 = bitcast(i32, value);
                var result: u32 = 0;
                if (signed < 0) { result |= 0x01; }
                if (signed >= 0) { result |= 0x02; }
                if (signed < other) { result |= 0x04; }
                return result;
            }

            reg var input_value: u32 = 0x80000000;
            reg var other_value: i32 = 5;
            reg var sink: u32 = 0;

            sink = compare_signed(input_value, other_value);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        IrBuildResult build = IrPipeline.Build(program);

        Assert.That(Regex.Matches(build.AssemblyText, @"(?m)^\s*TESTB\s+.*#31").Count, Is.EqualTo(2), build.AssemblyText);
        Assert.That(Regex.Matches(build.AssemblyText, @"(?m)^\s*CMPS\s+").Count, Is.EqualTo(1), build.AssemblyText);
        Assert.That(Regex.Matches(build.AssemblyText, @"(?m)^\s*CMP\s+").Count, Is.EqualTo(0), build.AssemblyText);
    }

    [Test]
    public void SignedComparisonLiteralMatcher_HandlesLiteralConvertedLiteralAndNonConstant()
    {
        Type contextType = typeof(MirLowerer).GetNestedType("FunctionLoweringContext", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing MirLowerer.FunctionLoweringContext.");
        MethodInfo method = contextType.GetMethod("TryGetIntegerLiteralValue", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing TryGetIntegerLiteralValue helper.");
        TextSpan span = new(0, 0);

        object?[] literalArgs = [new BoundLiteralExpression(BladeValue.IntegerLiteral(0), span), 0L];
        Assert.That(method.Invoke(null, literalArgs), Is.EqualTo(true));
        Assert.That(literalArgs[1], Is.EqualTo(0L));

        object?[] convertedArgs =
        [
            new BoundConversionExpression(new BoundLiteralExpression(BladeValue.IntegerLiteral(0), span), span, BuiltinTypes.I32),
            1L,
        ];
        Assert.That(method.Invoke(null, convertedArgs), Is.EqualTo(true));
        Assert.That(convertedArgs[1], Is.EqualTo(0L));

        VariableSymbol symbol = CreateVariableSymbol("value", BuiltinTypes.I32);
        object?[] nonConstantArgs = [new BoundSymbolExpression(symbol, span, BuiltinTypes.I32), 7L];
        Assert.That(method.Invoke(null, nonConstantArgs), Is.EqualTo(false));
        Assert.That(nonConstantArgs[1], Is.EqualTo(0L));

        FunctionSymbol function = new("helper", FunctionKind.Default);
        object?[] callArgs = [new BoundCallExpression(function, [], span, BuiltinTypes.I32), 9L];
        Assert.That(method.Invoke(null, callArgs), Is.EqualTo(false));
        Assert.That(callArgs[1], Is.EqualTo(0L));
    }

    [Test]
    public void NonPackedStructMemberAccess_PreservesAlignedByteOffsetInMir()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type Pair = struct {
                pad: u8,
                value: u32,
            };

            noinline fn demo() void {
                var pair: Pair = undefined;
                pair.value += 1;
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        MirModule mirModule = MirLowerer.Lower(program);
        string mir = MirTextWriter.Write(mirModule);

        Assert.That(mir, Does.Contain("load.member.value.4"));
        Assert.That(mir, Does.Contain("insert.member.value.4"));
    }

    [Test]
    public void NestedStructMemberAssignment_LowersRecursiveAggregateWriteback()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type Inner = struct {
                value: u32,
            };

            type Outer = struct {
                inner: Inner,
            };

            noinline fn demo() void {
                var outer: Outer = Outer { .inner = Inner { .value = 1 } };
                outer.inner.value = 2;
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        MirModule mirModule = MirLowerer.Lower(program);
        string mir = MirTextWriter.Write(mirModule);

        Assert.That(mir, Does.Contain("insert.member.value.0"));
        Assert.That(mir, Does.Contain("insert.member.inner.0"));
    }

    [Test]
    public void IndexedStructMemberAssignment_LowersIndexedAggregateWriteback()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type Pair = struct {
                value: u32,
            };

            hub var items: [1]Pair = undefined;
            items[0].value = 1;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        MirModule mirModule = MirLowerer.Lower(program);
        string mir = MirTextWriter.Write(mirModule);

        Assert.That(mir, Does.Contain("insert.member.value.0"));
        Assert.That(mir, Does.Contain("store index.hub"));
    }

    [Test]
    public void PointerStructMemberAssignment_LowersPointerAggregateWriteback()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type Pair = struct {
                value: u32,
            };

            hub var pair: Pair = undefined;
            var ptr: *hub Pair = &pair;
            ptr.*.value = 1;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        MirModule mirModule = MirLowerer.Lower(program);
        string mir = MirTextWriter.Write(mirModule);

        Assert.That(mir, Does.Contain("insert.member.value.0"));
        Assert.That(mir, Does.Contain("store deref.hub"));
    }

    [Test]
    public void AsmLowerer_InvalidBitfieldOpcodes_EmitComments()
    {
        TextSpan span = new(0, 0);
        LirVirtualRegister sourceRegister = LirRegister(0);
        LirVirtualRegister valueRegister = LirRegister(1);
        LirVirtualRegister destinationRegister = LirRegister(2);
        StructTypeSymbol bitfieldType = CreateStructType(
            "PackedBits",
            sizeBytes: 4,
            alignmentBytes: 4,
            new AggregateMemberSymbol("bits", BuiltinTypes.U32, byteOffset: 0, bitOffset: 2, bitWidth: 5, isBitfield: true));
        AggregateMemberSymbol unsupportedInsert = bitfieldType.Members["bits"];
        LirFunction function = CreateLirFunction(
            "demo",
            isEntryPoint: true,
            FunctionKind.Default,
            [],
            [
                new LirBlock(
                    LirBlockRef("bb0"),
                    [],
                    [
                        new LirOpInstruction(
                            new LirBitfieldInsertOperation(unsupportedInsert),
                            destinationRegister,
                            bitfieldType,
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

        Assert.That(asmir, Does.Contain("unhandled aligned fallback for bitfield.insert.2.5"));
    }

    [Test]
    public void AsmLowerer_UpdatePlaceSupportsExtendedReferenceOperators()
    {
        TextSpan span = new(0, 0);
        LirVirtualRegister valueRegister = LirRegister(0);
        StoragePlace accumulatorPlace = CreateStoragePlace("acc");
        LirFunction function = CreateLirFunction(
            "demo",
            isEntryPoint: true,
            FunctionKind.Default,
            [],
            [
                new LirBlock(
                    LirBlockRef("bb0"),
                    [],
                    [
                        new LirOpInstruction(
                            new LirUpdatePlaceOperation(BoundBinaryOperatorKind.Multiply),
                            destination: null,
                            resultType: null,
                            [new LirPlaceOperand(accumulatorPlace), new LirRegisterOperand(valueRegister)],
                            hasSideEffects: true,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            span),
                        new LirOpInstruction(
                            new LirUpdatePlaceOperation(BoundBinaryOperatorKind.Divide),
                            destination: null,
                            resultType: null,
                            [new LirPlaceOperand(accumulatorPlace), new LirRegisterOperand(valueRegister)],
                            hasSideEffects: true,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            span),
                        new LirOpInstruction(
                            new LirUpdatePlaceOperation(BoundBinaryOperatorKind.ArithmeticShiftLeft),
                            destination: null,
                            resultType: null,
                            [new LirPlaceOperand(accumulatorPlace), new LirRegisterOperand(valueRegister)],
                            hasSideEffects: true,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            span),
                        new LirOpInstruction(
                            new LirUpdatePlaceOperation(BoundBinaryOperatorKind.ArithmeticShiftRight),
                            destination: null,
                            resultType: null,
                            [new LirPlaceOperand(accumulatorPlace), new LirRegisterOperand(valueRegister)],
                            hasSideEffects: true,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            span),
                        new LirOpInstruction(
                            new LirUpdatePlaceOperation(BoundBinaryOperatorKind.RotateLeft),
                            destination: null,
                            resultType: null,
                            [new LirPlaceOperand(accumulatorPlace), new LirRegisterOperand(valueRegister)],
                            hasSideEffects: true,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            span),
                        new LirOpInstruction(
                            new LirUpdatePlaceOperation(BoundBinaryOperatorKind.RotateRight),
                            destination: null,
                            resultType: null,
                            [new LirPlaceOperand(accumulatorPlace), new LirRegisterOperand(valueRegister)],
                            hasSideEffects: true,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            span),
                    ],
                    new LirReturnTerminator([], span)),
            ]);

        AsmModule asmModule = AsmLowerer.Lower(new LirModule([accumulatorPlace], [], [function]));
        string asmir = AsmTextWriter.Write(asmModule);

        Assert.That(asmir, Does.Contain("QMUL"));
        Assert.That(asmir, Does.Contain("GETQX"));
        Assert.That(asmir, Does.Contain("QDIV"));
        Assert.That(asmir, Does.Contain("SAL"));
        Assert.That(asmir, Does.Contain("SAR"));
        Assert.That(asmir, Does.Contain("ROL"));
        Assert.That(asmir, Does.Contain("ROR"));
    }

    [Test]
    public void AsmLowerer_SingleWordStructOps_EmitAggregateInstructions()
    {
        TextSpan span = new(0, 0);
        LirVirtualRegister loRegister = LirRegister(0);
        LirVirtualRegister hiRegister = LirRegister(1);
        LirVirtualRegister midRegister = LirRegister(2);
        LirVirtualRegister destinationRegister = LirRegister(3);
        StructTypeSymbol packedType = CreateStructType(
            "Packed",
            sizeBytes: 4,
            alignmentBytes: 2,
            new AggregateMemberSymbol("lo", BuiltinTypes.U8, byteOffset: 0, bitOffset: 0, bitWidth: 0, isBitfield: false),
            new AggregateMemberSymbol("hi", BuiltinTypes.U8, byteOffset: 1, bitOffset: 0, bitWidth: 0, isBitfield: false),
            new AggregateMemberSymbol("mid", BuiltinTypes.U16, byteOffset: 2, bitOffset: 0, bitWidth: 0, isBitfield: false));
        StructTypeSymbol pairType = CreateStructType(
            "Pair",
            sizeBytes: 4,
            alignmentBytes: 4,
            new AggregateMemberSymbol("value", BuiltinTypes.U32, byteOffset: 0, bitOffset: 0, bitWidth: 0, isBitfield: false));
        AggregateMemberSymbol loMember = packedType.Members["lo"];
        AggregateMemberSymbol hiMember = packedType.Members["hi"];
        AggregateMemberSymbol midMember = packedType.Members["mid"];
        AggregateMemberSymbol valueMember = pairType.Members["value"];
        LirFunction function = CreateLirFunction(
            "demo",
            isEntryPoint: true,
            FunctionKind.Default,
            [],
            [
                new LirBlock(
                    LirBlockRef("bb0"),
                    [],
                    [
                        new LirOpInstruction(
                            new LirStructLiteralOperation([loMember, hiMember, midMember]),
                            destinationRegister,
                            packedType,
                            [new LirRegisterOperand(loRegister), new LirRegisterOperand(hiRegister), new LirRegisterOperand(midRegister)],
                            hasSideEffects: false,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            span),
                        new LirOpInstruction(
                            new LirLoadMemberOperation(hiMember),
                            destinationRegister,
                            BuiltinTypes.U8,
                            [new LirRegisterOperand(destinationRegister)],
                            hasSideEffects: false,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            span),
                        new LirOpInstruction(
                            new LirInsertMemberOperation(midMember),
                            destinationRegister,
                            packedType,
                            [new LirRegisterOperand(destinationRegister), new LirRegisterOperand(midRegister)],
                            hasSideEffects: false,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            span),
                        new LirOpInstruction(
                            new LirLoadMemberOperation(midMember),
                            destinationRegister,
                            BuiltinTypes.U16,
                            [new LirRegisterOperand(destinationRegister)],
                            hasSideEffects: false,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            span),
                        new LirOpInstruction(
                            new LirLoadMemberOperation(valueMember),
                            destinationRegister,
                            BuiltinTypes.U32,
                            [new LirRegisterOperand(destinationRegister)],
                            hasSideEffects: false,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            span),
                        new LirOpInstruction(
                            new LirInsertMemberOperation(valueMember),
                            destinationRegister,
                            pairType,
                            [new LirRegisterOperand(destinationRegister), new LirRegisterOperand(loRegister)],
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

        Assert.That(asmir, Does.Contain("SETBYTE"));
        Assert.That(asmir, Does.Contain("SETWORD"));
        Assert.That(asmir, Does.Contain("GETBYTE"));
        Assert.That(asmir, Does.Contain("ZEROX"));
        Assert.That(asmir, Does.Contain("MOV %r0, %r0"));
    }

    [Test]
    public void AsmLowerer_AggregateFeatureGaps_EmitComments()
    {
        TextSpan span = new(0, 0);
        LirVirtualRegister sourceRegister = LirRegister(0);
        LirVirtualRegister destinationRegister = LirRegister(1);
        StructTypeSymbol packedType = CreateStructType(
            "Packed",
            sizeBytes: 4,
            alignmentBytes: 1,
            new AggregateMemberSymbol("tag", BuiltinTypes.U8, byteOffset: 0, bitOffset: 0, bitWidth: 0, isBitfield: false),
            new AggregateMemberSymbol("value", BuiltinTypes.U16, byteOffset: 1, bitOffset: 0, bitWidth: 0, isBitfield: false));
        StructTypeSymbol wideType = CreateStructType(
            "Wide",
            sizeBytes: 8,
            alignmentBytes: 4,
            new AggregateMemberSymbol("left", BuiltinTypes.U32, byteOffset: 0, bitOffset: 0, bitWidth: 0, isBitfield: false),
            new AggregateMemberSymbol("right", BuiltinTypes.U32, byteOffset: 4, bitOffset: 0, bitWidth: 0, isBitfield: false));
        AggregateMemberSymbol wideLeftMember = wideType.Members["left"];
        AggregateMemberSymbol wideRightMember = wideType.Members["right"];
        AggregateMemberSymbol misalignedMember = packedType.Members["value"];
        LirFunction function = CreateLirFunction(
            "demo",
            isEntryPoint: true,
            FunctionKind.Default,
            [],
            [
                new LirBlock(
                    LirBlockRef("bb0"),
                    [],
                    [
                        new LirOpInstruction(
                            new LirLoadMemberOperation(wideRightMember),
                            destinationRegister,
                            BuiltinTypes.U32,
                            [new LirRegisterOperand(sourceRegister)],
                            hasSideEffects: false,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            span),
                        new LirOpInstruction(
                            new LirLoadMemberOperation(misalignedMember),
                            destinationRegister,
                            BuiltinTypes.U16,
                            [new LirRegisterOperand(sourceRegister)],
                            hasSideEffects: false,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            span),
                        new LirOpInstruction(
                            new LirInsertMemberOperation(wideRightMember),
                            destinationRegister,
                            wideType,
                            [new LirRegisterOperand(sourceRegister), new LirRegisterOperand(sourceRegister)],
                            hasSideEffects: false,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            span),
                        new LirOpInstruction(
                            new LirStructLiteralOperation([wideLeftMember, wideRightMember]),
                            destinationRegister,
                            wideType,
                            [new LirRegisterOperand(sourceRegister), new LirRegisterOperand(sourceRegister)],
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

        Assert.That(asmir, Does.Contain("unhandled: load.member.value.1"));
        Assert.That(asmir, Does.Not.Contain("unhandled: load.member.right.4"));
        Assert.That(asmir, Does.Not.Contain("unhandled: insert.member.right.4"));
        Assert.That(asmir, Does.Not.Contain("unhandled: structlit.left.right"));
    }

    private static IEnumerable<string> AcceptProgramsForPipeline()
    {
        string[] files =
        [
            "control_flow.blade",
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

    [Test]
    public void ImportedModuleGlobals_AreAllocatedOnceAcrossRepeatedModuleCalls()
    {
        using TempDirectory temp = new();
        temp.WriteFile("extmod.blade", """
            reg var seed: u32 = 7;
            seed = seed + 1;
            """);
        string sourcePath = temp.GetFullPath("main.blade");
        string source = """
            import extmod as ext;
            ext();
            ext();
            var after: u32 = ext.seed;
            """;

        CompilationResult compilation = CompilerDriver.Compile(
            source,
            sourcePath,
            new CompilationOptions
            {
                NamedModuleRoots = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["extmod"] = temp.GetFullPath("extmod.blade"),
                },
            });

        Assert.That(compilation.Diagnostics, Is.Empty, string.Join(Environment.NewLine, compilation.Diagnostics));
        Assert.That(compilation.IrBuildResult, Is.Not.Null);

        IReadOnlyList<StoragePlace> storagePlaces = compilation.IrBuildResult!.AsmModule.StoragePlaces;
        Assert.That(storagePlaces.Count(place => place.Symbol is VariableSymbol { Name: "seed" }), Is.EqualTo(1));
    }

    [Test]
    public void TopLevelAutomaticVariables_DoNotAllocateStoragePlaces()
    {
        LocalVariableSymbol symbol = new("local", BuiltinTypes.U32, isConst: false, sourceSpan: SourceSpan.Synthetic());
        BoundProgram program = IrTestFactory.CreateBoundProgram(
            constructorStatements:
            [
                new BoundVariableDeclarationStatement(symbol, new BoundLiteralExpression(new RuntimeBladeValue(BuiltinTypes.U32, 1L), new TextSpan(0, 0)), new TextSpan(0, 0)),
            ]);

        MirModule mirModule = MirLowerer.Lower(program);

        Assert.That(mirModule.StoragePlaces.Any(place => place.Symbol is VariableSymbol { Name: "local" }), Is.False);
    }

    [Test]
    public void DuplicateGlobalVariableSymbols_OnlyCreateOneMirStoragePlace()
    {
        GlobalVariableSymbol shared = (GlobalVariableSymbol)IrTestFactory.CreateVariableSymbol(
            "seed",
            BuiltinTypes.U32,
            VariableStorageClass.Reg,
            VariableScopeKind.GlobalStorage);
        BoundProgram program = IrTestFactory.CreateBoundProgram(globalVariables: [shared, shared]);

        MirModule mirModule = MirLowerer.Lower(program);

        Assert.That(mirModule.StoragePlaces.Count(place => ReferenceEquals(place.Symbol, shared)), Is.EqualTo(1));
    }

    [Test]
    public void DuplicateFileImportAliases_ShareImportedGlobalStorage()
    {
        using TempDirectory temp = new();
        temp.WriteFile("shared.mod", """
            reg var seed: u32 = 7;
            """);

        string sourcePath = temp.GetFullPath("main.blade");
        string source = """
            import "./shared.mod" as a;
            import "./shared.mod" as b;

            var left: u32 = a.seed;
            var right: u32 = b.seed;
            """;

        CompilationResult compilation = CompilerDriver.Compile(source, sourcePath, new CompilationOptions());

        Assert.That(compilation.Diagnostics, Is.Empty, string.Join(Environment.NewLine, compilation.Diagnostics));
        Assert.That(compilation.IrBuildResult, Is.Not.Null);

        IReadOnlyList<StoragePlace> storagePlaces = compilation.IrBuildResult!.AsmModule.StoragePlaces;
        Assert.That(storagePlaces.Count(place => place.Symbol is VariableSymbol { Name: "seed" }), Is.EqualTo(1));
    }

    private static StructTypeSymbol CreateStructType(
        string name,
        int sizeBytes,
        int alignmentBytes,
        params AggregateMemberSymbol[] members)
    {
        Dictionary<string, BladeType> fields = new(StringComparer.Ordinal);
        Dictionary<string, AggregateMemberSymbol> memberMap = new(StringComparer.Ordinal);
        foreach (AggregateMemberSymbol member in members)
        {
            fields[member.Name] = member.Type;
            memberMap[member.Name] = member;
        }

        return new StructTypeSymbol(name, fields, memberMap, sizeBytes, alignmentBytes);
    }

}
