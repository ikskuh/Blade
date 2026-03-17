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
        BoundProgram program = Binder.Bind(unit, diagnostics, source.FilePath, null);
        return (unit, program, diagnostics);
    }


    private static (CompilationUnitSyntax Unit, BoundProgram Program, DiagnosticBag Diagnostics) Bind(string text, string filePath, IReadOnlyDictionary<string, string>? namedModuleRoots = null)
    {
        SourceText source = new(text, filePath);
        DiagnosticBag diagnostics = new();
        Parser parser = Parser.Create(source, diagnostics);
        CompilationUnitSyntax unit = parser.ParseCompilationUnit();
        BoundProgram program = Binder.Bind(unit, diagnostics, filePath, namedModuleRoots);
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
    public void LocalConst_RuntimeInitializer_BindsAsConstVariable()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn demo(param: u32) void {
                const x: u32 = param * 2;
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");

        BoundFunctionMember function = program.Functions.Single();
        BoundBlockStatement body = function.Body;
        BoundVariableDeclarationStatement declaration = (BoundVariableDeclarationStatement)body.Statements[0];

        Assert.That(declaration.Symbol.IsConst, Is.True);
        Assert.That(declaration.Initializer, Is.TypeOf<BoundBinaryExpression>());
    }

    [Test]
    public void AssignmentToLocalConst_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn demo(param: u32) void {
                const x: u32 = param * 2;
                x = 3;
            }
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
    public void AddressOfLocalVariable_BindsRegisterPointerType()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn demo(param: u32) void {
                var x: u32 = param;
                var p: *reg u32 = &x;
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");

        BoundFunctionMember function = program.Functions.Single();
        BoundVariableDeclarationStatement declaration = (BoundVariableDeclarationStatement)function.Body.Statements[1];
        BoundUnaryExpression initializer = (BoundUnaryExpression)declaration.Initializer!;

        Assert.That(initializer.Operator.Kind, Is.EqualTo(BoundUnaryOperatorKind.AddressOf));
        Assert.That(initializer.Type.Name, Is.EqualTo("*reg u32"));
    }

    [Test]
    public void AddressOfParameter_BindsRegisterPointerType()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn demo(param: u32) void {
                var p: *reg u32 = &param;
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");

        BoundFunctionMember function = program.Functions.Single();
        BoundVariableDeclarationStatement declaration = (BoundVariableDeclarationStatement)function.Body.Statements[0];
        BoundUnaryExpression initializer = (BoundUnaryExpression)declaration.Initializer!;

        Assert.That(initializer.Operator.Kind, Is.EqualTo(BoundUnaryOperatorKind.AddressOf));
        Assert.That(initializer.Type.Name, Is.EqualTo("*reg u32"));
    }

    [Test]
    public void AddressOfNonName_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn demo() void {
                &(1 + 2);
            }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0223_InvalidAddressOfTarget), Is.True);
    }

    [Test]
    public void AddressOfRecursiveLocal_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            rec fn demo(bound: u32) void {
                var x: u32 = bound;
                var p: *reg u32 = &x;
            }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0226_AddressOfRecursiveLocal), Is.True);
    }

    [Test]
    public void UnaryPlusAndBitwiseNot_OnNonInteger_ReportDiagnostics()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn demo(flag: bool) void {
                +flag;
                ~flag;
            }
            """);

        Assert.That(diagnostics.Count(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.EqualTo(2));
    }

    [Test]
    public void ExplicitIntegerCast_BindsCastExpression()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn demo(x: u32) void {
                var y: u8 = x as u8;
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");

        BoundFunctionMember function = program.Functions.Single();
        BoundVariableDeclarationStatement declaration = (BoundVariableDeclarationStatement)function.Body.Statements[0];

        Assert.That(declaration.Initializer, Is.TypeOf<BoundCastExpression>());
        Assert.That(declaration.Initializer!.Type, Is.EqualTo(BuiltinTypes.U8));
    }

    [Test]
    public void ExplicitPointerCast_BindsCastExpression()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var value: u32 = 1;
            reg var source: *reg u32 = &value;
            reg var sink: *hub u32 = source as *hub u32;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");

        BoundGlobalVariableMember declaration = program.GlobalVariables.Single(global => global.Symbol.Name == "sink");
        Assert.That(declaration.Initializer, Is.TypeOf<BoundCastExpression>());
        Assert.That(declaration.Initializer!.Type.Name, Is.EqualTo("*hub u32"));
    }

    [Test]
    public void InvalidExplicitCast_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn demo(flag: bool) void {
                var value: u8 = flag as u8;
            }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0224_InvalidExplicitCast), Is.True);
    }

    [Test]
    public void Bitcast_BindsBitcastExpression()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var raw: u32 = 1;
            reg var ptr: *reg u32 = bitcast(*reg u32, raw);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");

        BoundGlobalVariableMember declaration = program.GlobalVariables.Single(global => global.Symbol.Name == "ptr");
        Assert.That(declaration.Initializer, Is.TypeOf<BoundBitcastExpression>());
        Assert.That(declaration.Initializer!.Type.Name, Is.EqualTo("*reg u32"));
    }

    [Test]
    public void BitcastSizeMismatch_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            reg var raw: u32 = 1;
            reg var narrowed: u16 = bitcast(u16, raw);
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0225_BitcastSizeMismatch), Is.True);
    }

    [Test]
    public void BitcastUnsupportedType_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn demo(flag: bool) void {
                var raw: u32 = bitcast(u32, flag);
            }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0224_InvalidExplicitCast), Is.True);
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
    public void MissingReturnValue_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn read_pin_to_carry(pin: u32) -> bool@C {
                asm {
                    TESTP {pin} WC
                } -> result: bool@C;
            }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0227_MissingReturnValue), Is.True);
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
    public void NamedArguments_AreReorderedToParameterOrder()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn pair(x: u32, y: u32) -> u32 {
                return x + y;
            }

            var result: u32 = pair(y=20, x=10);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");

        BoundVariableDeclarationStatement declaration = (BoundVariableDeclarationStatement)program.TopLevelStatements.Single();
        BoundCallExpression call = (BoundCallExpression)declaration.Initializer!;

        Assert.That(((BoundLiteralExpression)((BoundConversionExpression)call.Arguments[0]).Expression).Value, Is.EqualTo(10L));
        Assert.That(((BoundLiteralExpression)((BoundConversionExpression)call.Arguments[1]).Expression).Value, Is.EqualTo(20L));
    }

    [Test]
    public void MixedNamedArguments_AreReorderedToParameterOrder()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn pair(x: u32, y: u32) -> u32 {
                return x + y;
            }

            var result: u32 = pair(10, y=20);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");

        BoundVariableDeclarationStatement declaration = (BoundVariableDeclarationStatement)program.TopLevelStatements.Single();
        BoundCallExpression call = (BoundCallExpression)declaration.Initializer!;

        Assert.That(((BoundLiteralExpression)((BoundConversionExpression)call.Arguments[0]).Expression).Value, Is.EqualTo(10L));
        Assert.That(((BoundLiteralExpression)((BoundConversionExpression)call.Arguments[1]).Expression).Value, Is.EqualTo(20L));
    }

    [Test]
    public void UnknownNamedArgument_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn pair(x: u32, y: u32) -> u32 {
                return x + y;
            }

            var result: u32 = pair(z=10, y=20);
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0219_UnknownNamedArgument), Is.True);
    }

    [Test]
    public void DuplicateNamedArgument_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn pair(x: u32, y: u32) -> u32 {
                return x + y;
            }

            var result: u32 = pair(y=10, x=20, y=30);
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0220_DuplicateNamedArgument), Is.True);
    }

    [Test]
    public void PositionalArgumentAfterNamed_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn pair(x: u32, y: u32) -> u32 {
                return x + y;
            }

            var result: u32 = pair(y=10, 20);
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0221_PositionalArgumentAfterNamed), Is.True);
    }

    [Test]
    public void NamedArgumentConflictingWithPositional_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn pair(x: u32, y: u32) -> u32 {
                return x + y;
            }

            var result: u32 = pair(10, x=20);
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0222_NamedArgumentConflictsWithPositional), Is.True);
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

    [Test]
    public void FileImport_BindsModuleAliasMemberFunctionAndCall()
    {
        using TempDirectory temp = new();
        temp.WriteFile("math.blade", "fn inc(x: u32) -> u32 { return x + 1; } var seed: u32 = 1;");

        string sourcePath = temp.GetFullPath("main.blade");
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""import "./math.blade" as math; var outv: u32 = math.inc(2); math();""", sourcePath);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        Assert.That(program.ImportedModules.ContainsKey("math"), Is.True);
        Assert.That(program.ImportedModules["math"].ExportedFunctions.ContainsKey("inc"), Is.True);
    }

    [Test]
    public void NamedModuleImport_BindsAliasFromNamedModuleMap()
    {
        using TempDirectory temp = new();
        temp.WriteFile("ext.blade", "fn plus(a: u32, b: u32) -> u32 { return a + b; }");
        string modulePath = temp.GetFullPath("ext.blade");
        string sourcePath = temp.GetFullPath("main.blade");

        Dictionary<string, string> namedModules = new(StringComparer.Ordinal)
        {
            ["extmod"] = modulePath,
        };

        (_, _, DiagnosticBag diagnostics) = Bind("import extmod as ext; var v: u32 = ext.plus(1, 2);", sourcePath, namedModules);
        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
    }



    [Test]
    public void MissingImportFile_ReportsDiagnostic()
    {
        using TempDirectory temp = new();
        string sourcePath = temp.GetFullPath("main.blade");
        (_, _, DiagnosticBag diagnostics) = Bind("""import "./missing.mod" as missing;""", sourcePath);
        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0230_ImportFileNotFound), Is.True);
    }

    [Test]
    public void CircularImport_ReportsDiagnostic()
    {
        using TempDirectory temp = new();
        temp.MakeDir("circular");
        temp.WriteFile("circular/a.mod", """import "./b.mod" as b;""");
        temp.WriteFile("circular/b.mod", """import "./a.mod" as a;""");

        string sourcePath = temp.GetFullPath("main.blade");
        (_, _, DiagnosticBag diagnostics) = Bind("""import "./circular/a.mod" as a;""", sourcePath);
        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0231_CircularImport), Is.True);
    }

    [Test]
    public void FileImportWithoutAlias_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""import "./module.blade";""", "/tmp/main.blade");
        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0228_FileImportAliasRequired), Is.True);
    }

    [Test]
    public void UnknownNamedModule_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("import extmod;", "/tmp/main.blade", new Dictionary<string, string>());
        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0229_UnknownNamedModule), Is.True);
    }

}
