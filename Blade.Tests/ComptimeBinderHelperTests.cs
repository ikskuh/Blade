using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Blade.Diagnostics;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;
using Blade.Syntax;
using Blade.Syntax.Nodes;
using SemanticBinder = Blade.Semantics.Binder;

namespace Blade.Tests;

[TestFixture]
public sealed class ComptimeBinderHelperTests
{
    private static readonly TextSpan Span = new(0, 0);

    private static (BoundProgram Program, DiagnosticBag Diagnostics) Bind(string text, string? filePath = null, IReadOnlyDictionary<string, string>? namedModuleRoots = null)
    {
        SourceText source = filePath is null ? new SourceText(text) : new SourceText(text, filePath);
        DiagnosticBag diagnostics = new();
        Parser parser = Parser.Create(source, diagnostics);
        CompilationUnitSyntax unit = parser.ParseCompilationUnit();
        BoundProgram program = SemanticBinder.Bind(unit, diagnostics, source.FilePath, namedModuleRoots);
        return (program, diagnostics);
    }

    private static SemanticBinder CreateBinder(DiagnosticBag diagnostics)
    {
        ConstructorInfo constructor = typeof(SemanticBinder)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
        Type importedModuleDefinitionType = typeof(SemanticBinder).Assembly.GetType("Blade.Semantics.ImportedModuleDefinition", throwOnError: true)!;
        object moduleDefinitionCache = Activator.CreateInstance(
            typeof(Dictionary<,>).MakeGenericType(typeof(string), importedModuleDefinitionType),
            StringComparer.OrdinalIgnoreCase)!;
        return (SemanticBinder)constructor.Invoke(
        [
            diagnostics,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            moduleDefinitionCache,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            250,
        ]);
    }

    private static MethodInfo GetBinderStaticMethod(string name, params Type[] parameterTypes)
    {
        return typeof(SemanticBinder).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic, null, parameterTypes, null)!;
    }

    private static MethodInfo GetBinderStaticMethod(string name, int parameterCount)
    {
        return typeof(SemanticBinder).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == name && method.GetParameters().Length == parameterCount);
    }

    private static MethodInfo GetBinderInstanceMethod(string name, params Type[] parameterTypes)
    {
        return typeof(SemanticBinder).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, null, parameterTypes, null)!;
    }

    private static T Invoke<T>(MethodInfo method, object? instance, params object?[] args)
    {
        try
        {
            return (T)method.Invoke(instance, args)!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static object? Invoke(MethodInfo method, object? instance, params object?[] args)
    {
        try
        {
            return method.Invoke(instance, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static FunctionDeclarationSyntax ParseFunctionSyntax(string text)
    {
        SourceText source = new(text);
        DiagnosticBag diagnostics = new();
        Parser parser = Parser.Create(source, diagnostics);
        CompilationUnitSyntax unit = parser.ParseCompilationUnit();
        Assert.That(diagnostics.Count, Is.EqualTo(0), "Expected helper syntax to parse cleanly.");
        return unit.Members.OfType<FunctionDeclarationSyntax>().Single();
    }

    private static FunctionSymbol CreateFunctionSymbol(string name, FunctionKind kind, params TypeSymbol[] returnTypes)
    {
        FunctionDeclarationSyntax syntax = ParseFunctionSyntax($"fn {name}() -> u32 {{ return 0; }}");
        FunctionSymbol function = new(name, syntax, kind)
        {
            ReturnSlots = System.Array.ConvertAll(returnTypes, static t => new ReturnSlot(t, ReturnPlacement.Register)),
        };
        return function;
    }

    private static CompilationUnitSyntax EmptyCompilationUnit()
    {
        return new CompilationUnitSyntax([], new Token(TokenKind.EndOfFile, Span, string.Empty));
    }

    private static BoundProgram CreateProgram(
        IReadOnlyList<BoundFunctionMember>? functions = null,
        IReadOnlyDictionary<string, ImportedModule>? importedModules = null)
    {
        IReadOnlyList<BoundFunctionMember> functionMembers = functions ?? [];
        Dictionary<string, FunctionSymbol> functionLookup = functionMembers.ToDictionary(member => member.Symbol.Name, member => member.Symbol, StringComparer.Ordinal);
        return new BoundProgram(
            [],
            [],
            functionMembers,
            new Dictionary<string, TypeSymbol>(StringComparer.Ordinal),
            functionLookup,
            importedModules ?? new Dictionary<string, ImportedModule>(StringComparer.Ordinal));
    }

    private static ImportedModule CreateImportedModule(
        string alias,
        IReadOnlyList<BoundFunctionMember>? functions = null,
        IReadOnlyDictionary<string, ImportedModule>? importedModules = null)
    {
        BoundProgram program = CreateProgram(functions, importedModules);
        Dictionary<string, FunctionSymbol> exportedFunctions = program.FunctionLookup.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return new ImportedModule(
            alias,
            $"/tmp/{alias}.blade",
            alias,
            EmptyCompilationUnit(),
            program,
            exportedFunctions,
            new Dictionary<string, TypeSymbol>(StringComparer.Ordinal),
            new Dictionary<string, VariableSymbol>(StringComparer.Ordinal),
            importedModules ?? new Dictionary<string, ImportedModule>(StringComparer.Ordinal));
    }

    private static object CreateFailure(string kindName, string detail = "detail")
    {
        Assembly assembly = typeof(SemanticBinder).Assembly;
        Type failureKindType = assembly.GetType("Blade.Semantics.ComptimeFailureKind", throwOnError: true)!;
        Type failureType = assembly.GetType("Blade.Semantics.ComptimeFailure", throwOnError: true)!;
        object failureKind = Enum.Parse(failureKindType, kindName);
        return Activator.CreateInstance(failureType, failureKind, Span, detail)!;
    }

    private static object CreateUninitializedComptimeEvaluator()
    {
        Type evaluatorType = typeof(SemanticBinder).Assembly.GetType("Blade.Semantics.ComptimeEvaluator", throwOnError: true)!;
        return System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(evaluatorType);
    }

    private static string GetRecordField(object instance, string propertyName)
    {
        return instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)!.GetValue(instance)!.ToString()!;
    }

    [Test]
    public void StorageLayoutMetadata_RequiresComptimeInteger()
    {
        (_, DiagnosticBag diagnostics) = Bind("extern reg var DIRA: u32 @(true);");

        Assert.That(diagnostics.Any(diagnostic => diagnostic.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
    }

    [Test]
    public void ImportedPureFunctionCall_FoldsToLiteral()
    {
        using TempDirectory temp = new();
        temp.WriteFile("math.blade", "fn inc(x: u32) -> u32 { return x + 1; }");

        string sourcePath = temp.GetFullPath("main.blade");
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""import "./math.blade" as math; var outv: u32 = math.inc(2);""", sourcePath);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        BoundVariableDeclarationStatement statement = (BoundVariableDeclarationStatement)program.TopLevelStatements.Single();
        BoundLiteralExpression initializer = (BoundLiteralExpression)statement.Initializer!;
        Assert.That(initializer.Value, Is.EqualTo((uint)3));
    }

    [Test]
    public void ComptimeBareIfWithoutElse_FoldsBoolLiteralCondition()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            comptime fn choose() -> u32 {
                var value: u32 = 1;
                if (true) {
                    value = 2;
                }
                return value;
            }

            var result: u32 = choose();
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        BoundVariableDeclarationStatement statement = (BoundVariableDeclarationStatement)program.TopLevelStatements.Single();
        BoundLiteralExpression initializer = (BoundLiteralExpression)statement.Initializer!;
        Assert.That(initializer.Value, Is.EqualTo((uint)2));
    }

    [Test]
    public void StaticStorageConstants_AreReadableDuringFoldingAndComptimeEvaluation()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg const REG_RATE: u32 = 20_000_000;
            lut const LUT_OFFSET: u32 = 2;
            hub const HUB_OFFSET: u32 = 3;

            comptime fn total() -> u32 {
                return REG_RATE / 1_000_000 + LUT_OFFSET + HUB_OFFSET;
            }

            reg const DIRECT: u32 = REG_RATE / 1_000_000 + LUT_OFFSET + HUB_OFFSET;
            reg const VIA_FN: u32 = total();
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));
        Assert.That(program.GlobalVariables.Select(static global => global.Initializer).OfType<BoundLiteralExpression>().Select(static literal => literal.Value), Does.Contain((uint)25));
    }

    [Test]
    public void IntegerLiteralFolding_Keeps64BitIntermediatesUntilFinalMaterialization()
    {
        (BoundProgram program, DiagnosticBag diagnostics) = Bind("""
            reg const CLOCKS: u32 = 250 * 20_000_000 / 1000;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        BoundLiteralExpression initializer = (BoundLiteralExpression)program.GlobalVariables.Single().Initializer!;
        Assert.That(initializer.Value, Is.EqualTo((uint)5_000_000));
    }

    [Test]
    public void PrivateComptimeValidationHelpers_CoverRecursiveBranches()
    {
        MethodInfo method = GetBinderStaticMethod("TryValidateComptimeExpression", 2);
        VariableSymbol local = new("local", BuiltinTypes.U32, isConst: false, VariableStorageClass.Automatic, VariableScopeKind.Local, isExtern: false, fixedAddress: null, alignment: null);
        BoundExpression localSymbol = new BoundSymbolExpression(local, Span, BuiltinTypes.U32);
        FunctionSymbol function = CreateFunctionSymbol("callme", FunctionKind.Default, BuiltinTypes.U32);
        ImportedModule module = CreateImportedModule("mod");
        AggregateMemberSymbol member = new("value", BuiltinTypes.U32, byteOffset: 0, bitOffset: 0, bitWidth: 0, isBitfield: false);
        StructTypeSymbol pairType = new(
            "Pair",
            new Dictionary<string, TypeSymbol>(StringComparer.Ordinal) { ["value"] = BuiltinTypes.U32 },
            new Dictionary<string, AggregateMemberSymbol>(StringComparer.Ordinal) { ["value"] = member },
            sizeBytes: 4,
            alignmentBytes: 4);

        BoundExpression[] unsupportedExpressions =
        [
            new BoundUnaryExpression(BoundUnaryOperator.Bind(TokenKind.Ampersand)!, new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), Span, new PointerTypeSymbol(BuiltinTypes.U32, isConst: false)),
            new BoundBinaryExpression(new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Plus)!, localSymbol, Span, BuiltinTypes.IntegerLiteral),
            new BoundArrayLiteralExpression([new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), localSymbol], lastElementIsSpread: false, Span, new ArrayTypeSymbol(BuiltinTypes.U32, 2)),
            new BoundStructLiteralExpression([new BoundStructFieldInitializer("value", localSymbol)], Span, pairType),
            new BoundConversionExpression(localSymbol, Span, BuiltinTypes.U32),
            new BoundCastExpression(localSymbol, Span, BuiltinTypes.U32),
            new BoundBitcastExpression(localSymbol, Span, BuiltinTypes.U32),
            new BoundIfExpression(new BoundLiteralExpression(true, Span, BuiltinTypes.Bool), new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), localSymbol, Span, BuiltinTypes.IntegerLiteral),
            new BoundCallExpression(function, [], Span, BuiltinTypes.U32),
            new BoundModuleCallExpression(module, Span),
            new BoundIntrinsicCallExpression("encod", [], Span, BuiltinTypes.U32),
            new BoundMemberAccessExpression(new BoundStructLiteralExpression([], Span, pairType), member, Span),
            new BoundIndexExpression(new BoundLiteralExpression(0, Span, BuiltinTypes.IntegerLiteral), new BoundLiteralExpression(0, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.U32),
            new BoundPointerDerefExpression(new BoundLiteralExpression(0, Span, BuiltinTypes.U32), Span, BuiltinTypes.U32),
            new BoundRangeExpression(new BoundLiteralExpression(0, Span, BuiltinTypes.IntegerLiteral), localSymbol, false, Span),
            new BoundRangeExpression(new BoundLiteralExpression(0, Span, BuiltinTypes.IntegerLiteral), localSymbol, true, Span),
        ];

        foreach (BoundExpression expression in unsupportedExpressions)
        {
            object?[] args = [expression, null];
            bool isValid = Invoke<bool>(method, null, args);
            Assert.That(isValid, Is.False);
            Assert.That(args[1], Is.Not.Null);
        }
    }

    [Test]
    public void PrivateRequiresSuccessfulComptimeEvaluation_CoversCaseTable()
    {
        MethodInfo method = GetBinderStaticMethod("RequiresSuccessfulComptimeEvaluation", typeof(BoundExpression));
        EnumTypeSymbol mode = new("Mode", BuiltinTypes.U32, new Dictionary<string, long>(StringComparer.Ordinal) { ["On"] = 1 }, isOpen: false);
        FunctionSymbol function = CreateFunctionSymbol("callme", FunctionKind.Default, BuiltinTypes.U32);
        VariableSymbol local = new("local", BuiltinTypes.U32, isConst: false, VariableStorageClass.Automatic, VariableScopeKind.Local, isExtern: false, fixedAddress: null, alignment: null);
        UnionTypeSymbol unionType = new(
            "OneOf",
            new Dictionary<string, TypeSymbol>(StringComparer.Ordinal) { ["value"] = BuiltinTypes.U32 },
            new Dictionary<string, AggregateMemberSymbol>(StringComparer.Ordinal) { ["value"] = new AggregateMemberSymbol("value", BuiltinTypes.U32, 0, 0, 0, isBitfield: false) },
            sizeBytes: 4,
            alignmentBytes: 4);

        Assert.That(Invoke<bool>(method, null, new BoundLiteralExpression("text", Span, BuiltinTypes.String)), Is.False);
        Assert.That(Invoke<bool>(method, null, new BoundUnaryExpression(BoundUnaryOperator.Bind(TokenKind.Minus)!, new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.True);
        Assert.That(Invoke<bool>(method, null, new BoundBinaryExpression(new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Plus)!, new BoundLiteralExpression(2, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.True);
        Assert.That(Invoke<bool>(method, null, new BoundCallExpression(function, [], Span, BuiltinTypes.U32)), Is.True);
        Assert.That(Invoke<bool>(method, null, new BoundIfExpression(new BoundLiteralExpression(true, Span, BuiltinTypes.Bool), new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), new BoundLiteralExpression(2, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.True);
        Assert.That(Invoke<bool>(method, null, new BoundConversionExpression(new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.U32)), Is.True);
        Assert.That(Invoke<bool>(method, null, new BoundConversionExpression(new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), Span, new ArrayTypeSymbol(BuiltinTypes.U8, 1))), Is.False);
        Assert.That(Invoke<bool>(method, null, new BoundCastExpression(new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.U8)), Is.True);
        Assert.That(Invoke<bool>(method, null, new BoundCastExpression(new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), Span, unionType)), Is.False);
        Assert.That(Invoke<bool>(method, null, new BoundBitcastExpression(new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.I32)), Is.True);
        Assert.That(Invoke<bool>(method, null, new BoundSymbolExpression(local, Span, BuiltinTypes.U32)), Is.True);
        Assert.That(Invoke<bool>(method, null, new BoundArrayLiteralExpression([], lastElementIsSpread: false, Span, new ArrayTypeSymbol(BuiltinTypes.U8, 0))), Is.False);
    }

    [Test]
    public void PrivateConstantIntegerHelpers_CoverConversionsAndOperators()
    {
        Type comptimeResultType = typeof(SemanticBinder).Assembly.GetType("Blade.Semantics.ComptimeResult", throwOnError: true)!;
        MethodInfo tryEvaluateConstantValue = GetBinderInstanceMethod("TryEvaluateConstantValue", typeof(BoundExpression), comptimeResultType.MakeByRefType());
        MethodInfo tryEvaluateConstantInt = GetBinderInstanceMethod("TryEvaluateConstantInt", typeof(BoundExpression));
        MethodInfo tryConvertConstantToInt64 = GetBinderStaticMethod("TryConvertConstantToInt64", comptimeResultType, typeof(long).MakeByRefType());
        MethodInfo tryGetBool = comptimeResultType.GetMethod("TryGetBool")!;
        SemanticBinder binder = CreateBinder(new DiagnosticBag());

        object?[] boolArgs =
        [
            new BoundBinaryExpression(
                new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral),
                BoundBinaryOperator.Bind(TokenKind.EqualEqual)!,
                new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral),
                Span,
                BuiltinTypes.Bool),
            null,
        ];
        Assert.That((bool)tryEvaluateConstantValue.Invoke(binder, boolArgs)!, Is.True);
        object comptimeBool = boolArgs[1]!;
        object?[] tryGetBoolArgs = [false];
        Assert.That((bool)tryGetBool.Invoke(comptimeBool, tryGetBoolArgs)!, Is.True);
        Assert.That(tryGetBoolArgs[0], Is.EqualTo(true));

        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundLiteralExpression((ulong)7, Span, BuiltinTypes.U32)), Is.EqualTo(7));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundLiteralExpression((short)8, Span, BuiltinTypes.I16)), Is.EqualTo(8));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundLiteralExpression((ushort)9, Span, BuiltinTypes.U16)), Is.EqualTo(9));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundLiteralExpression((byte)10, Span, BuiltinTypes.U8)), Is.EqualTo(10));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundLiteralExpression((sbyte)(-11), Span, BuiltinTypes.I8)), Is.EqualTo(-11));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundConversionExpression(new BoundLiteralExpression(12, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.U32)), Is.EqualTo(12));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundCastExpression(new BoundLiteralExpression(255, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.U8)), Is.EqualTo(255));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBitcastExpression(new BoundLiteralExpression(255, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.I8)), Is.EqualTo(-1));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundUnaryExpression(BoundUnaryOperator.Bind(TokenKind.Minus)!, new BoundLiteralExpression(5, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(-5));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundUnaryExpression(BoundUnaryOperator.Bind(TokenKind.Tilde)!, new BoundLiteralExpression(5, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(~5));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundUnaryExpression(BoundUnaryOperator.Bind(TokenKind.Plus)!, new BoundLiteralExpression(5, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(5));

        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(new BoundLiteralExpression(7, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Plus)!, new BoundLiteralExpression(2, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(9));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(new BoundLiteralExpression(7, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Minus)!, new BoundLiteralExpression(2, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(5));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(new BoundLiteralExpression(7, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Star)!, new BoundLiteralExpression(2, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(14));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(new BoundLiteralExpression(7, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Slash)!, new BoundLiteralExpression(2, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(3));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(new BoundLiteralExpression(7, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Slash)!, new BoundLiteralExpression(0, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.Null);
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(new BoundLiteralExpression(7, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Percent)!, new BoundLiteralExpression(2, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(1));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(new BoundLiteralExpression(7, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Percent)!, new BoundLiteralExpression(0, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.Null);
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(new BoundLiteralExpression(6, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Ampersand)!, new BoundLiteralExpression(3, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(2));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(new BoundLiteralExpression(6, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Pipe)!, new BoundLiteralExpression(3, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(7));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(new BoundLiteralExpression(6, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Caret)!, new BoundLiteralExpression(3, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(5));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(new BoundLiteralExpression(3, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.LessLess)!, new BoundLiteralExpression(2, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(12));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(new BoundLiteralExpression(8, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.GreaterGreater)!, new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(4));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(new BoundLiteralExpression(3, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.LessLessLess)!, new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(6));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(new BoundLiteralExpression(8, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.GreaterGreaterGreater)!, new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(4));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.RotateLeft)!, new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(2));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(new BoundLiteralExpression(2, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.RotateRight)!, new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(1));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.EqualEqual)!, new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.Bool)), Is.Null);

        object undefined = comptimeResultType.GetField("Undefined", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        object intValue = Activator.CreateInstance(comptimeResultType, 1)!;
        object uintValue = Activator.CreateInstance(comptimeResultType, (uint)2)!;
        object overflowValue = Activator.CreateInstance(comptimeResultType, long.MaxValue)!;
        object?[] nullArgs = [undefined, 0L];
        object?[] intArgs = [intValue, 0L];
        object?[] uintArgs = [uintValue, 0L];
        object?[] overflowArgs = [overflowValue, 0L];

        Assert.That((bool)tryConvertConstantToInt64.Invoke(null, nullArgs)!, Is.False);
        Assert.That((bool)tryConvertConstantToInt64.Invoke(null, intArgs)!, Is.True);
        Assert.That(intArgs[1], Is.EqualTo(1L));
        Assert.That((bool)tryConvertConstantToInt64.Invoke(null, uintArgs)!, Is.True);
        Assert.That(uintArgs[1], Is.EqualTo(2L));
        Assert.That((bool)tryConvertConstantToInt64.Invoke(null, overflowArgs)!, Is.True);
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundLiteralExpression((long)int.MaxValue + 1L, Span, BuiltinTypes.IntegerLiteral)), Is.Null);
    }

    [Test]
    public void PrivateComptimeResolutionAndReportingHelpers_CoverImportedLookupAndDiagnostics()
    {
        DiagnosticBag diagnostics = new();
        SemanticBinder binder = CreateBinder(diagnostics);
        FieldInfo boundBodiesField = typeof(SemanticBinder).GetField("_boundFunctionBodies", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo importedModulesField = typeof(SemanticBinder).GetField("_importedModules", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Dictionary<FunctionSymbol, BoundBlockStatement> boundBodies = (Dictionary<FunctionSymbol, BoundBlockStatement>)boundBodiesField.GetValue(binder)!;
        Dictionary<string, ImportedModule> importedModules = (Dictionary<string, ImportedModule>)importedModulesField.GetValue(binder)!;
        MethodInfo resolveBody = GetBinderInstanceMethod("ResolveFunctionBodyForComptime", typeof(FunctionSymbol));
        MethodInfo reportFailure = GetBinderInstanceMethod("ReportComptimeFailure", CreateFailure("NotEvaluable").GetType());

        FunctionSymbol localFunction = CreateFunctionSymbol("local", FunctionKind.Default);
        BoundBlockStatement localBody = new([], Span);
        boundBodies.Add(localFunction, localBody);
        Assert.That(Invoke(resolveBody, binder, localFunction), Is.SameAs(localBody));

        FunctionSymbol importedFunction = CreateFunctionSymbol("imported", FunctionKind.Default);
        BoundBlockStatement importedBody = new([], Span);
        ImportedModule nestedModule = CreateImportedModule("nested", [new BoundFunctionMember(importedFunction, importedBody, Span)]);
        ImportedModule rootModule = CreateImportedModule(
            "root",
            functions: [],
            importedModules: new Dictionary<string, ImportedModule>(StringComparer.Ordinal) { ["nested"] = nestedModule });
        importedModules.Add("root", rootModule);

        Assert.That(Invoke(resolveBody, binder, importedFunction), Is.SameAs(importedBody));

        Invoke(reportFailure, binder, CreateFailure("NotEvaluable"));
        Invoke(reportFailure, binder, CreateFailure("UnsupportedConstruct", "unsupported"));
        Invoke(reportFailure, binder, CreateFailure("ForbiddenSymbolAccess", "forbidden"));
        Invoke(reportFailure, binder, CreateFailure("FuelExhausted"));

        Assert.That(diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(
        [
            DiagnosticCode.E0241_ComptimeValueRequired,
            DiagnosticCode.E0242_ComptimeUnsupportedConstruct,
            DiagnosticCode.E0243_ComptimeForbiddenSymbolAccess,
            DiagnosticCode.E0244_ComptimeFuelExhausted,
        ]));
    }
}
