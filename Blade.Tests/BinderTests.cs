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



    [Test]
    public void StructMemberAccess_UnknownFieldReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            type P = packed struct { x: u32 };
            var p: P = .{ .x = 1 };
            var y: u32 = p.missing;
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0202_UndefinedName), Is.True);
    }


    [Test]
    public void DuplicateImportAlias_ReportsDiagnostic()
    {
        using TempDirectory temp = new();
        temp.WriteFile("ext.blade", "fn plus(a: u32, b: u32) -> u32 { return a + b; }");
        string sourcePath = temp.GetFullPath("main.blade");
        Dictionary<string, string> namedModules = new(StringComparer.Ordinal)
        {
            ["extmod"] = temp.GetFullPath("ext.blade"),
        };

        (_, _, DiagnosticBag diagnostics) = Bind("import extmod as ext; import extmod as ext;", sourcePath, namedModules);
        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0201_SymbolAlreadyDeclared), Is.True);
    }

    [Test]
    public void ImportedModule_MetadataIncludesTypesAndPropertiesAreReadable()
    {
        using TempDirectory temp = new();
        temp.WriteFile("types.blade", "type Alias = u32; fn id(x: u32) -> u32 { return x; }");
        string sourcePath = temp.GetFullPath("main.blade");

        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""import "./types.blade" as t;""", sourcePath);
        Assert.That(diagnostics.Count, Is.EqualTo(0));

        ImportedModule module = program.ImportedModules["t"];
        Assert.That(module.SourceName, Is.EqualTo("./types.blade"));
        Assert.That(module.ResolvedFilePath, Is.EqualTo(temp.GetFullPath("types.blade")));
        Assert.That(module.DefaultAlias, Is.EqualTo("t"));
        Assert.That(module.Syntax, Is.Not.Null);
        Assert.That(module.ExportedTypes.ContainsKey("Alias"), Is.True);
    }

    [Test]
    public void ModuleMemberAccess_UnknownMemberReportsDiagnostic()
    {
        using TempDirectory temp = new();
        temp.WriteFile("math.blade", "fn inc(x: u32) -> u32 { return x + 1; }");
        string sourcePath = temp.GetFullPath("main.blade");

        (_, _, DiagnosticBag diagnostics) = Bind("""import "./math.blade" as math; var y: u32 = math.missing;""", sourcePath);
        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0202_UndefinedName), Is.True);
    }

    [Test]
    public void ModuleCall_WithArgumentsReportsArgumentCountMismatch()
    {
        using TempDirectory temp = new();
        temp.WriteFile("math.blade", "fn inc(x: u32) -> u32 { return x + 1; }");
        string sourcePath = temp.GetFullPath("main.blade");

        (_, _, DiagnosticBag diagnostics) = Bind("""import "./math.blade" as math; math(1);""", sourcePath);
        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0207_ArgumentCountMismatch), Is.True);
    }

    [Test]
    public void AddressOfArray_BindsRegisterMultiPointerType()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn demo() void {
                var values: [4]u32 = undefined;
                var p: [*]reg u32 = &values;
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");

        BoundFunctionMember function = program.Functions.Single();
        BoundVariableDeclarationStatement declaration = (BoundVariableDeclarationStatement)function.Body.Statements[1];
        Assert.That(declaration.Initializer!.Type.Name, Is.EqualTo("[*]reg u32"));
    }

    [Test]
    public void AddressOfArrayParameter_BindsRegisterMultiPointerType()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn demo(values: [4]u32) void {
                var p: [*]reg u32 = &values;
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");

        BoundFunctionMember function = program.Functions.Single();
        BoundVariableDeclarationStatement declaration = (BoundVariableDeclarationStatement)function.Body.Statements[0];
        Assert.That(declaration.Initializer!.Type.Name, Is.EqualTo("[*]reg u32"));
    }

    [Test]
    public void AddressOfFunction_ReportsInvalidTarget()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn demo() void {
                &demo;
            }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0223_InvalidAddressOfTarget), Is.True);
    }

    [Test]
    public void AddressOfMissingName_ReportsUndefinedName()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn demo() void {
                &missing;
            }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0202_UndefinedName), Is.True);
    }

    [Test]
    public void AddressOfRecursiveParameter_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            rec fn demo(value: u32) void {
                var p: *reg u32 = &value;
            }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0226_AddressOfRecursiveLocal), Is.True);
    }

    [Test]
    public void PointerIndexing_ReportsDiagnosticForSinglePointer()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn demo(p: *reg u32) -> u32 {
                return p[0];
            }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
    }

    [Test]
    public void PointerIndexAssignment_ReportsDiagnosticForSinglePointerAndNonIntegerIndex()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn demo(p: *reg u32, values: [4]u32) void {
                p[0] = 1;
                values[false] = 2;
            }
            """);

        Assert.That(diagnostics.Count(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void MultiPointerDeref_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn demo(p: [*]reg u32) -> u32 {
                return p.*;
            }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
    }

    [Test]
    public void AssignmentToFunction_ReportsInvalidAssignmentTargetAndWritesTargetError()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn demo() void {
            }

            demo = 1;
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0106_InvalidAssignmentTarget), Is.True);
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("TargetError"));
    }

    [Test]
    public void InvalidLiteralAssignmentTarget_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            1 = 2;
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0106_InvalidAssignmentTarget), Is.True);
    }

    [Test]
    public void PointerQualifiers_AllowAddingConstAndVolatile()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            reg var source: *reg u32 = undefined;
            reg var sink: *reg const volatile u32 = source;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
    }

    [Test]
    public void PointerQualifiers_RejectDroppingConstOrVolatile()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            reg var source: *reg const volatile u32 = undefined;
            reg var sink: *reg u32 = source;
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
    }

    [Test]
    public void PointerAlignment_AllowsStrongerSourceAlignment()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            reg var source: *reg align(8) u32 = undefined;
            reg var sink: *reg align(4) u32 = source;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
    }

    [Test]
    public void PointerAlignment_RejectsWeakerSourceAlignment()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            reg var source: *reg align(4) u32 = undefined;
            reg var sink: *reg align(8) u32 = source;
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
    }

    [Test]
    public void PointerAssignment_RejectsFamilyMismatch()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            reg var source: [*]reg u32 = undefined;
            reg var sink: *reg u32 = source;
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
    }

    [Test]
    public void EnumLiteral_BindsFromExpectedContext()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type Mode = enum (u8) {
                Idle = 0,
                Busy = 1,
            };

            reg var mode: Mode = .Busy;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("EnumLiteral<Mode> .Busy = 1"));
    }

    [Test]
    public void QualifiedEnumMember_BindsWithoutValueScopeEntry()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type Mode = enum (u8) {
                Idle = 0,
                Busy = 1,
            };

            reg var mode: Mode = Mode.Busy;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("EnumLiteral<Mode> .Busy = 1"));
    }

    [Test]
    public void BareEnumLiteral_WithoutContextReportsDiagnostic()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type Mode = enum (u8) {
                Idle = 0,
            };

            .Idle;
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0232_EnumLiteralRequiresContext), Is.True);
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("ErrorExpr"));
    }

    [Test]
    public void MissingTypeAliasQualifiedMember_ReportsUndefinedName()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            reg var value: u32 = MissingAlias.Member;
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0202_UndefinedName), Is.True);
    }

    [Test]
    public void EnumLiteral_UnknownMemberReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            type Mode = enum (u8) {
                Idle = 0,
            };

            reg var mode: Mode = .Missing;
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0202_UndefinedName), Is.True);
    }

    [Test]
    public void QualifiedEnumMember_UnknownMemberReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            type Mode = enum (u8) {
                Idle = 0,
            };

            reg var mode: Mode = Mode.Missing;
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0202_UndefinedName), Is.True);
    }

    [Test]
    public void CrossEnumAssignment_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            type First = enum (u8) { A = 0, };
            type Second = enum (u8) { A = 0, };

            reg var first: First = .A;
            reg var second: Second = first;
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
    }

    [Test]
    public void OpenEnumExplicitCast_BindsButClosedEnumExplicitCastReportsDiagnostic()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type OpenState = enum (u8) {
                Idle = 0,
                ...,
            };

            type ClosedState = enum (u8) {
                Idle = 0,
            };

            reg var raw: u8 = ((2 as u8) as OpenState) as u8;
            reg var closed: ClosedState = .Idle;
            reg var bad: u8 = closed as u8;
            """);

        Assert.That(program.GlobalVariables.Single(global => global.Symbol.Name == "raw").Initializer, Is.TypeOf<BoundCastExpression>());
        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0224_InvalidExplicitCast), Is.True);
    }

    [Test]
    public void ClosedEnumBitcast_BindsAsBitcastExpression()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type ClosedState = enum (u8) {
                Idle = 0,
            };

            reg var closed: ClosedState = .Idle;
            reg var raw: u8 = bitcast(u8, closed);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        Assert.That(program.GlobalVariables.Single(global => global.Symbol.Name == "raw").Initializer, Is.TypeOf<BoundBitcastExpression>());
    }

    [Test]
    public void EnumMembers_AutoIncrementAndRejectDuplicates()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type Mode = enum (u8) {
                Idle,
                Busy = 5,
                Done,
                Done,
            };
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0201_SymbolAlreadyDeclared), Is.True);
        EnumTypeSymbol mode = (EnumTypeSymbol)program.TypeAliases["Mode"];
        Assert.That(mode.Members["Idle"], Is.EqualTo(0));
        Assert.That(mode.Members["Busy"], Is.EqualTo(5));
        Assert.That(mode.Members["Done"], Is.EqualTo(6));
    }

    [Test]
    public void EnumType_RejectsNonIntegerBackingAndNonConstantMemberValues()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            reg var seed: u32 = 1;

            type BadBacking = enum (bool) {
                A = 0,
            };

            type BadValue = enum (u8) {
                A = seed,
            };
            """);

        Assert.That(diagnostics.Count(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void EnumArithmetic_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            type Mode = enum (u8) {
                Idle = 0,
                Busy = 1,
            };

            fn demo(mode: Mode) -> u32 {
                return mode + 1;
            }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
    }

    [Test]
    public void EmptyAggregateAndEnumAliases_BindWithoutDiagnostics()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type EmptyStruct = packed struct {
            };

            type EmptyUnion = union {
            };

            type EmptyEnum = enum (u8) {
            };

            type EmptyFlags = bitfield (u32) {
            };

            reg var s: EmptyStruct = undefined;
            reg var u: EmptyUnion = undefined;
            reg var e: EmptyEnum = undefined;
            reg var f: EmptyFlags = undefined;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        Assert.That(program.TypeAliases.Keys, Is.EquivalentTo(new[] { "EmptyStruct", "EmptyUnion", "EmptyEnum", "EmptyFlags" }));
    }

    [Test]
    public void AnonymousUnionAndBitfieldTypes_BindWithGeneratedNames()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn demo() void {
                var header: union {
                    empty: void,
                    raw: u32,
                } = undefined;

                var flags: bitfield (u32) {
                    low: nib,
                    high: nib,
                } = undefined;
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");

        BoundFunctionMember function = program.Functions.Single();
        BoundVariableDeclarationStatement headerDeclaration = (BoundVariableDeclarationStatement)function.Body.Statements[0];
        BoundVariableDeclarationStatement flagsDeclaration = (BoundVariableDeclarationStatement)function.Body.Statements[1];

        Assert.That(headerDeclaration.Symbol.Type.Name, Does.StartWith("<anon-union#"));
        Assert.That(flagsDeclaration.Symbol.Type.Name, Does.StartWith("<anon-bitfield#"));
    }

    [Test]
    public void DuplicateUnionAndBitfieldMembers_ReportDiagnostics()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            type Header = union {
                lo: u32,
                lo: u16,
            };

            type Flags = bitfield (u32) {
                low: nib,
                low: bit,
            };
            """);

        Assert.That(diagnostics.Count(d => d.Code == DiagnosticCode.E0201_SymbolAlreadyDeclared), Is.EqualTo(2));
    }

    [Test]
    public void UnionMemberAccess_BindsLikeStruct()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type Header = union {
                lo: u32,
                hi: u32,
            };

            reg var header: Header = undefined;
            reg var value: u32 = header.lo;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("Member<u32> .lo"));
    }

    [Test]
    public void UnknownUnionMemberAssignment_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            type Header = union {
                lo: u32,
            };

            reg var header: Header = undefined;
            header.hi = 1;
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0202_UndefinedName), Is.True);
    }

    [Test]
    public void UnionAssignment_UsesStructuralCompatibility()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            type First = union {
                lo: u32,
                hi: u32,
            };

            type Second = union {
                lo: u32,
                hi: u32,
            };

            reg var first: First = undefined;
            reg var second: Second = first;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
    }

    [Test]
    public void UnionAssignment_RejectsMismatchedShapes()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            type First = union {
                lo: u32,
            };

            type Second = union {
                hi: u32,
            };

            reg var first: First = undefined;
            reg var second: Second = first;
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
    }

    [Test]
    public void BitfieldOverflow_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            type Flags = bitfield (u8) {
                wide: u16,
            };
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0233_BitfieldWidthOverflow), Is.True);
    }

    [Test]
    public void BitfieldType_RejectsNonIntegerBackingAndNonScalarFields()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            type BadBacking = bitfield (bool) {
                flag: bool,
            };

            type BadFields = bitfield (u32) {
                nested: void,
            };
            """);

        Assert.That(diagnostics.Count(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void CrossBitfieldAssignment_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            type Left = bitfield (u32) {
                low: nib,
            };

            type Right = bitfield (u32) {
                low: nib,
            };

            reg var left: Left = undefined;
            reg var right: Right = left;
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
    }

    [Test]
    public void BitfieldMemberAssignment_BindsDedicatedTarget()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type Flags = bitfield (u32) {
                low: nib,
                high: nib,
            };

            reg var flags: Flags = undefined;
            flags.high = 3;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("TargetBitfield<nib> .high"));
    }

    [Test]
    public void BitfieldPostIncrement_BindsThroughBitfieldTargetReadPath()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type Flags = bitfield (u32) {
                low: nib,
                high: nib,
            };

            reg var flags: Flags = undefined;
            flags.high++;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("Unary<nib> PostIncrement"));
        Assert.That(dump, Does.Contain("Member<nib> .high"));
    }

    [Test]
    public void PostfixMemberAndInvalidTargets_CoverRemainingBranches()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type Pair = packed struct {
                value: u32,
            };

            fn demo() void {
            }

            reg var pair: Pair = .{ .value = 1 };
            pair.value--;
            demo++;
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0106_InvalidAssignmentTarget), Is.True);
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("Member<u32> .value"));
        Assert.That(dump, Does.Contain("ErrorExpr"));
    }

    [Test]
    public void PostfixIncrement_CoversIndexAndDerefTargetsAndRejectsBool()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn demo(ptr: *reg u32) void {
                var values: [2]u32 = undefined;
                var flag: bool = false;
                values[0]++;
                ptr.*++;
                flag++;
            }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("Index<u32>"));
        Assert.That(dump, Does.Contain("Deref<u32>"));
    }

    [Test]
    public void ArrayLiteral_BindsFromElementInference()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn demo() void {
                [1, 2, 3];
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        BoundFunctionMember function = program.Functions.Single();
        BoundExpressionStatement statement = (BoundExpressionStatement)function.Body.Statements[0];
        BoundArrayLiteralExpression literal = (BoundArrayLiteralExpression)statement.Expression;
        Assert.That(literal.Type.Name, Is.EqualTo("[3]<int-literal>"));
        Assert.That(literal.Elements.Count, Is.EqualTo(3));
    }

    [Test]
    public void ArrayLiteral_UsesExpectedElementTypeAndSpread()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var values: [4]u32 = [1, 2...];
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        BoundGlobalVariableMember declaration = program.GlobalVariables.Single();
        Assert.That(declaration.Initializer, Is.TypeOf<BoundArrayLiteralExpression>());
        BoundArrayLiteralExpression literal = (BoundArrayLiteralExpression)declaration.Initializer!;
        Assert.That(literal.Type.Name, Is.EqualTo("[4]u32"));
        Assert.That(literal.LastElementIsSpread, Is.True);
        Assert.That(literal.Elements.Count, Is.EqualTo(2));
    }

    [Test]
    public void ArrayLiteral_WithNonConstantContextLengthBindsLengthlessArrayType()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            fn demo(count: u32) void {
                var values: [count]u32 = [1, 2, 3];
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        BoundFunctionMember function = program.Functions.Single();
        BoundVariableDeclarationStatement declaration = (BoundVariableDeclarationStatement)function.Body.Statements[0];
        BoundArrayLiteralExpression literal = (BoundArrayLiteralExpression)declaration.Initializer!;
        Assert.That(literal.Type.Name, Is.EqualTo("[u32]"));
        Assert.That(literal.Type.Length, Is.Null);
    }

    [Test]
    public void EmptyArrayLiteral_RequiresContextAndBindsWithExpectedType()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var values: [3]u32 = [];
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        BoundArrayLiteralExpression literal = (BoundArrayLiteralExpression)program.GlobalVariables.Single().Initializer!;
        Assert.That(literal.Type.Name, Is.EqualTo("[3]u32"));
        Assert.That(literal.Elements, Is.Empty);
    }

    [Test]
    public void ArrayLiteral_BoundTreeWriterMarksSpreadElement()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var values: [4]u32 = [1, 2...];
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("ArrayLit<[4]u32>"));
        Assert.That(dump, Does.Contain("[1]..."));
    }

    [Test]
    public void ArrayLiteral_ElementTypeMismatchReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            reg var values: [2]u32 = [1, false];
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
    }

    [Test]
    public void ArrayLiteral_SpreadMustBeLastReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            reg var values: [4]u32 = [1..., 2];
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0235_ArrayLiteralSpreadMustBeLast), Is.True);
    }

    [Test]
    public void ArrayLiteral_EmptyOrSpreadWithoutContextReportDiagnostics()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            [];
            [1...];
            """);

        Assert.That(diagnostics.Count(d => d.Code == DiagnosticCode.E0234_ArrayLiteralRequiresContext), Is.EqualTo(2));
    }

    [Test]
    public void TypedStructLiteral_BindsCorrectly()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            type Point = packed struct { x: u32, y: u32 };
            var p: Point = Point { .x = 10, .y = 20 };
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("StructLit<Point>"));
    }

    [Test]
    public void TypedStructLiteral_UnknownField_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            type Point = packed struct { x: u32, y: u32 };
            var p: Point = Point { .x = 10, .z = 20 };
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0236_StructUnknownField), Is.True);
    }

    [Test]
    public void TypedStructLiteral_MissingField_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            type Point = packed struct { x: u32, y: u32 };
            var p: Point = Point { .x = 10 };
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0237_StructMissingFields), Is.True);
    }

    [Test]
    public void TypedStructLiteral_DuplicateField_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            type Point = packed struct { x: u32, y: u32 };
            var p: Point = Point { .x = 1, .x = 2, .y = 3 };
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0238_StructDuplicateField), Is.True);
    }

    [Test]
    public void TypedStructLiteral_NonStructType_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            type Alias = u32;
            var x: u32 = Alias { .x = 10 };
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
    }

    [Test]
    public void TypedStructLiteral_UndefinedType_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            var x: u32 = Unknown { .x = 10 };
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0203_UndefinedType), Is.True);
    }

    [Test]
    public void ForLoop_CountOnly_BindsCorrectly()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var count: u32 = 4;
            reg var sink: u32 = 0;
            for (count) { sink = sink + 1; }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("For\n"));
    }

    [Test]
    public void ForLoop_CountWithIndex_BindsCorrectly()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg var count: u32 = 4;
            reg var sink: u32 = 0;
            for (count) -> i { sink = sink + i; }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("For -> i"));
    }

    [Test]
    public void ForLoop_ArrayWithItem_BindsCorrectly()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            var arr: [4]u32 = [1,2,3,4];
            reg var sink: u32 = 0;
            for (arr) -> x { sink = sink + x; }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("For -> x"));
    }

    [Test]
    public void ForLoop_ArrayWithMutableItem_BindsCorrectly()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            var arr: [4]u32 = [1,2,3,4];
            for (arr) -> &x { x = 10; }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("For -> &x"));
    }

    [Test]
    public void ForLoop_ArrayWithMutableItemAndIndex_BindsCorrectly()
    {
        (_, BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            var arr: [4]u32 = [1,2,3,4];
            for (arr) -> &x, i { x = i; }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("For -> &x, i"));
    }

    [Test]
    public void ForLoop_NonIterableType_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            var x: bool = true;
            for (x) { }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
    }

    [Test]
    public void ForLoop_MutableRefOnCount_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            reg var count: u32 = 4;
            for (count) -> &i { }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
    }

    [Test]
    public void ForLoop_NonIterableWithBinding_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            var x: bool = true;
            for (x) -> item { }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
    }


}
