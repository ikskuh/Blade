using System.Linq;
using Blade;
using Blade.Diagnostics;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Syntax;
using Blade.Syntax.Nodes;
using DiagnosticBag = System.Collections.Generic.IReadOnlyList<Blade.Diagnostics.Diagnostic>;

namespace Blade.Tests;

[TestFixture]
public class BinderTests
{
    private static (CompilationUnitSyntax Unit, BoundModule Program, IReadOnlyList<Diagnostic> Diagnostics) Bind(string text)
    {
        CompilationResult result = CompilerDriver.Compile(text, filePath: "<input>", new CompilationOptions
        {
            EmitIr = false,
        });
        return (result.Syntax, result.BoundModule, result.Diagnostics);
    }


    private static (CompilationUnitSyntax Unit, BoundModule Program, IReadOnlyList<Diagnostic> Diagnostics) Bind(string text, string filePath, IReadOnlyDictionary<string, string>? namedModuleRoots = null)
    {
        CompilationResult result = CompilerDriver.Compile(text, filePath, new CompilationOptions
        {
            NamedModuleRoots = namedModuleRoots ?? new Dictionary<string, string>(StringComparer.Ordinal),
            EmitIr = false,
        });
        return (result.Syntax, result.BoundModule, result.Diagnostics);
    }

    [Test]
    public void TypedAssignment_InsertsImplicitConversionForIntegerLiteral()
    {
        (_, BoundModule program, IReadOnlyList<Diagnostic> diagnostics) = Bind("reg var x: u32 = 0; x = 1;");

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("Literal<u32> 1"));
    }

    [Test]
    public void TypedCallArgument_InsertsImplicitConversionForIntegerLiteral()
    {
        (_, BoundModule program, IReadOnlyList<Diagnostic> diagnostics) = Bind("""
            fn add(a: u32, b: u32) -> u32 {
                return a + b;
            }
            var result: u32 = add(1, 2);
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("Literal<u32> 3"));
    }

    [Test]
    public void UndefinedName_ReportsDiagnostic()
    {
        (_, _, IReadOnlyList<Diagnostic> diagnostics) = Bind("x = 1;");
        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0202_UndefinedName), Is.True);
    }

    [Test]
    public void AssertFalse_ReportsAssertionFailedDiagnostic()
    {
        (_, _, IReadOnlyList<Diagnostic> diagnostics) = Bind("assert false;");

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0255_AssertionFailed), Is.True);
    }

    [Test]
    public void AssertNonComptimeCondition_ReportsComptimeValueRequired()
    {
        (_, _, IReadOnlyList<Diagnostic> diagnostics) = Bind("""
            var x: u32 = 1;
            assert x == 1;
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0241_ComptimeValueRequired), Is.True);
    }

    [Test]
    public void CompileTimeKnownNarrowing_ReportsTruncationWarning()
    {
        (_, _, IReadOnlyList<Diagnostic> diagnostics) = Bind("""
            reg const wide: u32 = 257;
            reg const implicit_small: u8 = wide;
            reg const explicit_small: u8 = 257 as u8;
            reg const exact_small: u8 = 255;
            """);

        Assert.That(diagnostics.Count(static diagnostic => diagnostic.Code == DiagnosticCode.W0261_ComptimeIntegerTruncation), Is.EqualTo(2));
    }

    [Test]
    public void AssignmentToConst_ReportsDiagnostic()
    {
        (_, _, IReadOnlyList<Diagnostic> diagnostics) = Bind("""
            reg const x: u32 = 1;
            x = 2;
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0204_CannotAssignToConstant), Is.True);
    }

    [Test]
    public void LocalConst_RuntimeInitializer_BindsAsConstVariable()
    {
        (_, BoundModule program, IReadOnlyList<Diagnostic> diagnostics) = Bind("""
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
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
            fn demo(param: u32) void {
                var x: u32 = param;
                var p: *reg u32 = &x;
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");

        BoundFunctionMember function = program.Functions.Single();
        BoundVariableDeclarationStatement declaration = (BoundVariableDeclarationStatement)function.Body.Statements[1];
        BoundLiteralExpression initializer = (BoundLiteralExpression)declaration.Initializer!;

        Assert.That(initializer.Type.Name, Is.EqualTo("*reg u32"));
        Assert.That(initializer.Value.TryGetPointedValue(out PointedValue pointedValue), Is.True);
        Assert.That(pointedValue.Symbol.Name, Is.EqualTo("x"));
        Assert.That(pointedValue.Offset, Is.EqualTo(0));
    }

    [Test]
    public void AddressOfParameter_BindsRegisterPointerType()
    {
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
            fn demo(param: u32) void {
                var p: *reg u32 = &param;
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");

        BoundFunctionMember function = program.Functions.Single();
        BoundVariableDeclarationStatement declaration = (BoundVariableDeclarationStatement)function.Body.Statements[0];
        BoundLiteralExpression initializer = (BoundLiteralExpression)declaration.Initializer!;

        Assert.That(initializer.Type.Name, Is.EqualTo("*reg u32"));
        Assert.That(initializer.Value.TryGetPointedValue(out PointedValue pointedValue), Is.True);
        Assert.That(pointedValue.Symbol.Name, Is.EqualTo("param"));
        Assert.That(pointedValue.Offset, Is.EqualTo(0));
    }

    [Test]
    public void AddressOfNonName_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn demo() void {
                var p: *reg u32 = &(1 + 2);
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
                var a: u32 = +flag;
                var b: u32 = ~flag;
            }
            """);

        Assert.That(diagnostics.Count(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.EqualTo(2));
    }

    [Test]
    public void ExplicitIntegerCast_BindsCastExpression()
    {
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
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
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
            fn demo() void {
                var value: u32 = 1;
                var source: *reg u32 = &value;
                var sink: *hub u32 = source as *hub u32;
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");

        BoundFunctionMember function = program.Functions.Single();
        BoundVariableDeclarationStatement declaration = (BoundVariableDeclarationStatement)function.Body.Statements[2];
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
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
            fn demo() void {
                var raw: u32 = 1;
                var ptr: *reg u32 = bitcast(*reg u32, raw);
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");

        BoundFunctionMember function = program.Functions.Single();
        BoundVariableDeclarationStatement declaration = (BoundVariableDeclarationStatement)function.Body.Statements[1];
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
            fn demo() void {
                var raw: u32 = bitcast(u32, [1, 2]);
            }
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0224_InvalidExplicitCast), Is.True);
    }

    [Test]
    public void BreakInsideRepLoop_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            rep loop {
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
    public void FunctionWithoutReturnSpec_BindsAsVoid()
    {
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
            fn empty_call() {
            }

            empty_call();
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        Assert.That(program.Functions.Single().Symbol.ReturnTypes, Is.Empty);
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
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
            fn pair(x: u32, y: u32) -> u32 {
                return x + y;
            }

            fn demo(input: u32) -> u32 {
                return pair(y=20, x=input);
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");

        BoundFunctionMember function = program.Functions.Single(member => member.Symbol.Name == "demo");
        BoundReturnStatement returnStatement = (BoundReturnStatement)function.Body.Statements.Single();
        BoundCallExpression call = (BoundCallExpression)returnStatement.Values.Single();

        Assert.That(call.Arguments[0], Is.TypeOf<BoundSymbolExpression>());
        Assert.That(((BoundLiteralExpression)call.Arguments[1]).Value.Value, Is.EqualTo((uint)20));
    }

    [Test]
    public void MixedNamedArguments_AreReorderedToParameterOrder()
    {
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
            fn pair(x: u32, y: u32) -> u32 {
                return x + y;
            }

            fn demo(input: u32) -> u32 {
                return pair(input, y=20);
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");

        BoundFunctionMember function = program.Functions.Single(member => member.Symbol.Name == "demo");
        BoundReturnStatement returnStatement = (BoundReturnStatement)function.Body.Statements.Single();
        BoundCallExpression call = (BoundCallExpression)returnStatement.Values.Single();

        Assert.That(call.Arguments[0], Is.TypeOf<BoundSymbolExpression>());
        Assert.That(((BoundLiteralExpression)call.Arguments[1]).Value.Value, Is.EqualTo((uint)20));
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
            type A = struct { x: u32, };
            type A = struct { y: u32, };
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
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
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
    public void FileImport_BindsModuleAliasMemberFunctionAndCall()
    {
        using TempDirectory temp = new();
        temp.WriteFile("math.blade", "fn inc(x: u32) -> u32 { return x + 1; } var seed: u32 = 1;");

        string sourcePath = temp.GetFullPath("main.blade");
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""import "./math.blade" as math; var outv: u32 = math.inc(2); math();""", sourcePath);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        Assert.That(program.ExportedSymbols.TryGetValue("math", out Symbol? exportedSymbol), Is.True);
        Assert.That(exportedSymbol, Is.TypeOf<ModuleSymbol>());
        ModuleSymbol mathModule = (ModuleSymbol)exportedSymbol!;
        Assert.That(mathModule.Module.ExportedSymbols.ContainsKey("inc"), Is.True);
    }

    [Test]
    public void StructMemberAccess_UnknownFieldReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            type P = struct { x: u32 };
            var p: P = P { .x = 1 };
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

        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""import "./types.blade" as t;""", sourcePath);
        Assert.That(diagnostics.Count, Is.EqualTo(0));

        ModuleSymbol importedModule = (ModuleSymbol)program.ExportedSymbols["t"];
        BoundModule module = importedModule.Module;
        Assert.That(module.ResolvedFilePath, Is.EqualTo(temp.GetFullPath("types.blade")));
        Assert.That(module.Syntax, Is.Not.Null);
        Assert.That(module.ExportedSymbols.ContainsKey("Alias"), Is.True);
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
    public void ModuleMemberAssignment_UnknownMemberReportsDiagnostic()
    {
        using TempDirectory temp = new();
        temp.WriteFile("math.blade", "fn inc(x: u32) -> u32 { return x + 1; }");
        string sourcePath = temp.GetFullPath("main.blade");

        (_, _, DiagnosticBag diagnostics) = Bind("""import "./math.blade" as math; math.missing = 1;""", sourcePath);
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
    public void LogicalOperators_WithNonBoolOperandsReportTypeMismatch()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            var value: bool = 1 and 2;
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
    }

    [Test]
    public void AddressOfArray_BindsRegisterMultiPointerType()
    {
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
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
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
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
    public void AddressOfMissingName_ReportsUndefinedName()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn demo() void {
                var p: *reg u32 = &missing;
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
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
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
            fn demo() void {
                var source: *reg u32 = undefined;
                var sink: *reg const volatile u32 = source;
            }
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
            fn demo() void {
                var source: *reg align(8) u32 = undefined;
                var sink: *reg align(4) u32 = source;
            }
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
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
            type Mode = enum (u8) {
                Idle = 0,
                Busy = 1,
            };

            reg var mode: Mode = .Busy;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("Literal<Mode> 1"));
    }

    [Test]
    public void QualifiedEnumMember_BindsWithoutValueScopeEntry()
    {
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
            type Mode = enum (u8) {
                Idle = 0,
                Busy = 1,
            };

            reg var mode: Mode = Mode.Busy;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("Literal<Mode> 1"));
    }

    [Test]
    public void BareEnumLiteral_WithoutContextReportsDiagnostic()
    {
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
            type Mode = enum (u8) {
                Idle = 0,
            };

            reg var x: u32 = .Idle;
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
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
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

        Assert.That(program.GlobalVariables.Single(global => global.Name == "raw").Initializer, Is.TypeOf<BoundLiteralExpression>());
        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0224_InvalidExplicitCast), Is.True);
    }

    [Test]
    public void ClosedEnumBitcast_BindsAsBitcastExpression()
    {
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
            type ClosedState = enum (u8) {
                Idle = 0,
            };

            fn demo() void {
                var closed: ClosedState = .Idle;
                var raw: u8 = bitcast(u8, closed);
            }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        BoundFunctionMember function = program.Functions.Single();
        BoundVariableDeclarationStatement declaration = (BoundVariableDeclarationStatement)function.Body.Statements[1];
        Assert.That(declaration.Initializer, Is.TypeOf<BoundBitcastExpression>());
    }

    [Test]
    public void EnumMembers_AutoIncrementAndRejectDuplicates()
    {
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
            type Mode = enum (u8) {
                Idle,
                Busy = 5,
                Done,
                Done,
            };
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0201_SymbolAlreadyDeclared), Is.True);
        EnumTypeSymbol mode = (EnumTypeSymbol)((TypeSymbol)program.ExportedSymbols["Mode"]).Type;
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
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
            type EmptyStruct = struct {
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
        Assert.That(program.ExportedSymbols.Values.OfType<TypeSymbol>().Select(symbol => symbol.Name), Is.EquivalentTo(new[] { "EmptyStruct", "EmptyUnion", "EmptyEnum", "EmptyFlags" }));
    }

    [Test]
    public void NonPackedStruct_AlignsFieldsAndRoundsSize()
    {
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
            type P = struct { x: u8, y: u32 };
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");

        StructTypeSymbol p = (StructTypeSymbol)((TypeSymbol)program.ExportedSymbols["P"]).Type;

        Assert.Multiple(() =>
        {
            Assert.That(p.Members["x"].ByteOffset, Is.EqualTo(0));
            Assert.That(p.Members["y"].ByteOffset, Is.EqualTo(4));
            Assert.That(p.SizeBytes, Is.EqualTo(8));
            Assert.That(p.AlignmentBytes, Is.EqualTo(4));
        });
    }

    [Test]
    public void DistinctStructTypes_AreNotAssignable()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            type P = struct { x: u8, y: u32 };
            type Q = struct { x: u8, y: u32 };

            var p: P = undefined;
            var q: Q = p;
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
    }

    [Test]
    public void AnonymousUnionAndBitfieldTypes_BindWithGeneratedNames()
    {
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
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
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
            type Header = union {
                lo: u32,
                hi: u32,
            };

            var header: Header = undefined;
            var value: u32 = header.lo;
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
    public void DistinctUnionTypes_AreNotAssignable()
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

            var first: First = undefined;
            var second: Second = first;
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
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
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
    public void ArrayLiteral_BindsFromElementInference()
    {
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
            fn demo() void {
                [1, 2, 3];
            }
            """);

        Assert.That(diagnostics.Select(d => d.Code), Is.EqualTo(new[] { DiagnosticCode.E0259_ExpressionNotAStatement }));
        Assert.That(program.Functions, Is.Empty);
    }

    [Test]
    public void ArrayLiteral_UsesExpectedElementTypeAndSpread()
    {
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
            reg var values: [4]u32 = [1, 2...];
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected no diagnostics.");
        GlobalVariableSymbol declaration = program.GlobalVariables.Single();
        Assert.That(declaration.Initializer, Is.TypeOf<BoundArrayLiteralExpression>());
        BoundArrayLiteralExpression literal = (BoundArrayLiteralExpression)declaration.Initializer!;
        Assert.That(literal.Type.Name, Is.EqualTo("[4]u32"));
        Assert.That(literal.LastElementIsSpread, Is.True);
        Assert.That(literal.Elements.Count, Is.EqualTo(2));
    }

    [Test]
    public void ArrayLiteral_WithNonConstantContextLengthBindsLengthlessArrayType()
    {
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
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
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
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
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
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

        Assert.That(diagnostics.Select(d => d.Code), Is.EqualTo(new[]
        {
            DiagnosticCode.E0259_ExpressionNotAStatement,
            DiagnosticCode.E0259_ExpressionNotAStatement,
        }));
    }

    [Test]
    public void TypedStructLiteral_BindsCorrectly()
    {
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
            type Point = struct { x: u32, y: u32 };
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
            type Point = struct { x: u32, y: u32 };
            var p: Point = Point { .x = 10, .z = 20 };
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0236_StructUnknownField), Is.True);
    }

    [Test]
    public void TypedStructLiteral_MissingField_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            type Point = struct { x: u32, y: u32 };
            var p: Point = Point { .x = 10 };
            """);

        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0237_StructMissingFields), Is.True);
    }

    [Test]
    public void TypedStructLiteral_DuplicateField_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            type Point = struct { x: u32, y: u32 };
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
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("""
            reg var count: u32 = 4;
            reg var sink: u32 = 0;
            for (count) { sink = sink + 1; }
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("For\n"));
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

    [Test]
    public void RangeExpression_OutsideLoop_ReportsDiagnostic()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("""
            fn demo() void {
                _ = 1..<2;
            }
            """);

        Assert.That(diagnostics.Select(d => d.Code), Is.EqualTo(new[] { DiagnosticCode.E0263_RangeExpressionOutsideForLoop }));
    }

    // --- CS-9: Character and string literal tests ---

    [Test]
    public void CharLiteral_BindsAsIntegerLiteral()
    {
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("reg var x: u32 = 'A';");
        Assert.That(diagnostics.Count, Is.EqualTo(0));
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("65"));
    }

    [Test]
    public void CharLiteral_EscapeSequence_BindsCorrectly()
    {
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("reg var x: u32 = '\\n';");
        Assert.That(diagnostics.Count, Is.EqualTo(0));
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("10"));
    }

    [Test]
    public void StringLiteral_CoercesToByteArray_WhenLengthMatches()
    {
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("reg var a: [4]u8 = \"bye!\";");
        Assert.That(diagnostics.Count, Is.EqualTo(0));
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("Literal<[4]u8> [98, 121, 101, 33]"));
    }

    [Test]
    public void ZeroTerminatedString_CoercesToByteArray_WithNul()
    {
        // z"hi!" is 3 chars + NUL = 4 bytes, matching [4]u8
        (_, BoundModule program, DiagnosticBag diagnostics) = Bind("reg var a: [4]u8 = z\"hi!\";");
        Assert.That(diagnostics.Count, Is.EqualTo(0));
        string dump = BoundTreeWriter.Write(program);
        Assert.That(dump, Does.Contain("Literal<[4]u8> [104, 105, 33, 0]"));
    }

    [Test]
    public void StringLiteral_LengthMismatch_ReportsTypeMismatch()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("reg var a: [3]u8 = \"bye!\";");
        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
    }

    [Test]
    public void ZeroTerminatedString_LengthMismatch_ReportsTypeMismatch()
    {
        // z"hi!" is 4 bytes (3 + NUL), does not fit in [3]u8
        (_, _, DiagnosticBag diagnostics) = Bind("reg var a: [3]u8 = z\"hi!\";");
        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
    }

    [Test]
    public void StringLiteral_ToNonConstPointer_ReportsE0240()
    {
        (_, _, DiagnosticBag diagnostics) = Bind("reg var s: [*]reg u8 = \"hello\";");
        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0240_StringToNonConstPointer), Is.True);
    }

    [Test]
    public void StringLiteral_ToConstPointer_IsAssignable()
    {
        // Verify binding succeeds (even though backend lowering is not implemented)
        (_, _, DiagnosticBag diagnostics) = Bind("reg var s: [*]reg const u8 = \"hello\";");
        // Should not report type mismatch or string-to-non-const errors
        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0205_TypeMismatch), Is.False);
        Assert.That(diagnostics.Any(d => d.Code == DiagnosticCode.E0240_StringToNonConstPointer), Is.False);
    }


}
