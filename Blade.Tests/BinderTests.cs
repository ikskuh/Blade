using System.Linq;
using Blade.Diagnostics;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;
using Blade.Syntax;
using Blade.Syntax.Nodes;

namespace Blade.Tests;

[TestFixture]
public class BinderTests
{
    private static (CompilationUnitSyntax Unit, BoundProgram Program, DiagnosticBag Diagnostics) Bind(string text)
    {
        SourceText source = new(text);
        DiagnosticBag diagnostics = new();
        Parser parser = Parser.Create(source, diagnostics);
        CompilationUnitSyntax unit = parser.ParseCompilationUnit();
        BoundProgram program = Binder.Bind(unit, diagnostics);
        return (unit, program, diagnostics);
    }

    [Test]
    public void TypedAssignment_InsertsImplicitConversionForIntegerLiteral()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("reg var x: u32 = 0; x = 1;");

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("Conversion<u32>"));
    }

    [Test]
    public void TypedCallArgument_InsertsImplicitConversionForIntegerLiteral()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn add(a: u32, b: u32) -> u32 {
                return a + b;
            }
            var result: u32 = add(1, 2);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("Call<u32> add"));
        Assert.That(dump, Does.Contain("Conversion<u32>"));
    }

    [Test]
    public void UndefinedName_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("x = 1;");
        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0202_UndefinedName), Is.True);
    }

    [Test]
    public void AssignmentToConst_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            reg const x: u32 = 1;
            x = 2;
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0204_CannotAssignToConstant), Is.True);
    }

    [Test]
    public void DuplicateLocalVariable_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn f() void {
                var x: u32 = 0;
                var x: u32 = 1;
            }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0201_SymbolAlreadyDeclared), Is.True);
    }

    [Test]
    public void BreakInsideRepLoop_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            rep loop (4) {
                break;
            }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0209_InvalidBreakInRepLoop), Is.True);
    }

    [Test]
    public void YieldOutsideInterruptFunction_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn f() void {
                yield;
            }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0210_InvalidYieldUsage), Is.True);
    }

    [Test]
    public void YieldtoOutsideCoroutineContext_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            coro fn worker() void { loop { yieldto worker(); } }
            fn f() void {
                yieldto worker();
            }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0211_InvalidYieldtoUsage), Is.True);
    }

    [Test]
    public void ReturnCountMismatch_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn f() -> u32 {
                return;
            }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0212_ReturnValueCountMismatch), Is.True);
    }

    [Test]
    public void CallArgumentCountMismatch_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn add(a: u32, b: u32) -> u32 {
                return a + b;
            }

            reg var x: u32 = add(1);
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0207_ArgumentCountMismatch), Is.True);
    }

    [Test]
    public void UndefinedTypeAlias_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("reg var x: MissingType = undefined;");
        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0203_UndefinedType), Is.True);
    }

    [Test]
    public void DuplicateTypeAlias_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            const A = packed struct { x: u32, };
            const A = packed struct { y: u32, };
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0201_SymbolAlreadyDeclared), Is.True);
    }

    [Test]
    public void TopLevelVar_IsNotVisibleInsideFunctions()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            var counter: u32 = 0;

            fn f() void {
                counter = 1;
            }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0202_UndefinedName), Is.True);
    }

    [Test]
    public void AsmVolatile_PropagatesToBoundTree()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn f(x: u32) void {
                asm volatile {
                    MOV {x}, {x}
                };
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("Asm [Volatile]"));
    }

    [Test]
    public void BoundTreeWriter_CoversAdvancedStatementsAndExpressions()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            const Pair = packed struct { left: u32, right: u32 };

            coro fn worker(seed: u32) -> u32 {
                var pair: Pair = .{ .left = seed, .right = seed };
                var arr: [2]u32 = undefined;
                var ptr: *u32 = undefined;
                var sink: u32 = 0;

                while (true) { break; }
                loop { continue; }
                rep loop (2) { sink = sink + 1; }
                rep for (i in 1..2) { sink = sink + i; }
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
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        string dump = BoundTreeWriter.Write(program);

        Assert.That(dump, Does.Contain("TypeAliases"));
        Assert.That(dump, Does.Contain("Yieldto (worker)"));
        Assert.That(dump, Does.Contain("Yield"));
        Assert.That(dump, Does.Contain("While"));
        Assert.That(dump, Does.Contain("Loop"));
        Assert.That(dump, Does.Contain("RepLoop"));
        Assert.That(dump, Does.Contain("RepFor (i)"));
        Assert.That(dump, Does.Contain("Noirq"));
        Assert.That(dump, Does.Contain("Break"));
        Assert.That(dump, Does.Contain("Continue"));
        Assert.That(dump, Does.Contain("Member<u32> .left"));
        Assert.That(dump, Does.Contain("Index<u32>"));
        Assert.That(dump, Does.Contain("Deref<u32>"));
        Assert.That(dump, Does.Contain("IfExpr<u32>"));
        Assert.That(dump, Does.Contain("Range<range>"));
        Assert.That(dump, Does.Contain("StructLit<Pair>"));
        Assert.That(dump, Does.Contain("TargetMember<u32> .right"));
        Assert.That(dump, Does.Contain("TargetIndex<u32>"));
        Assert.That(dump, Does.Contain("TargetDeref<u32>"));
        Assert.That(dump, Does.Contain("Intrinsic<u32> @encod"));
        Assert.That(dump, Does.Contain("Asm [NonVolatile]"));
    }
}
