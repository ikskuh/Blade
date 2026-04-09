using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Blade;
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

    private static (BoundModule Program, IReadOnlyList<Diagnostic> Diagnostics) Bind(string text, string? filePath = null, IReadOnlyDictionary<string, string>? namedModuleRoots = null)
    {
        string effectivePath = filePath ?? "<input>";
        CompilationResult result = CompilerDriver.Compile(text, effectivePath, new CompilationOptions
        {
            NamedModuleRoots = namedModuleRoots ?? new Dictionary<string, string>(StringComparer.Ordinal),
            EmitIr = false,
        });
        return (result.BoundModule, result.Diagnostics);
    }

    private static SemanticBinder CreateBinder(DiagnosticBag diagnostics)
    {
        ConstructorInfo constructor = typeof(SemanticBinder)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
        Assembly asm = typeof(SemanticBinder).Assembly;

        Type loadedCompilationType = asm.GetType("Blade.LoadedCompilation", throwOnError: true)!;
        Type loadedModuleType = asm.GetType("Blade.LoadedModule", throwOnError: true)!;
        Type loadedImportType = asm.GetType("Blade.LoadedImport", throwOnError: true)!;

        SourceText rootSource = new(string.Empty, "<input>");
        CompilationUnitSyntax rootSyntax = EmptyCompilationUnit();
        string rootFullPath = Path.GetFullPath(rootSource.FilePath);

        Array emptyImports = Array.CreateInstance(loadedImportType, 0);
        object rootModule = Activator.CreateInstance(
            loadedModuleType,
            rootFullPath,
            rootSource,
            rootSyntax,
            0,
            emptyImports)!;

        object modulesByPath = Activator.CreateInstance(
            typeof(Dictionary<,>).MakeGenericType(typeof(string), loadedModuleType),
            StringComparer.OrdinalIgnoreCase)!;
        ((IDictionary)modulesByPath).Add(rootFullPath, rootModule);

        object loadedCompilation = Activator.CreateInstance(
            loadedCompilationType,
            rootModule,
            modulesByPath)!;

        Type importedModuleDefinitionType = typeof(SemanticBinder).Assembly.GetType("Blade.Semantics.ImportedModuleDefinition", throwOnError: true)!;
        object moduleDefinitionCache = Activator.CreateInstance(
            typeof(Dictionary<,>).MakeGenericType(typeof(string), importedModuleDefinitionType),
            StringComparer.OrdinalIgnoreCase)!;
        return (SemanticBinder)constructor.Invoke(
        [
            diagnostics,
            loadedCompilation,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            moduleDefinitionCache,
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

    private static FunctionSymbol CreateFunctionSymbol(string name, FunctionKind kind, params BladeType[] returnTypes)
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

    private static BoundModule CreateProgram(
        IReadOnlyList<BoundFunctionMember>? functions = null,
        IReadOnlyDictionary<string, BoundModule>? importedModules = null)
    {
        IReadOnlyList<BoundFunctionMember> functionMembers = functions ?? [];
        Dictionary<string, FunctionSymbol> functionLookup = functionMembers.ToDictionary(member => member.Symbol.Name, member => member.Symbol, StringComparer.Ordinal);
        return new BoundModule(
            "/tmp/test.blade",
            EmptyCompilationUnit(),
            [],
            [],
            functionMembers,
            new Dictionary<string, TypeSymbol>(StringComparer.Ordinal),
            functionLookup,
            new Dictionary<string, VariableSymbol>(StringComparer.Ordinal),
            importedModules ?? new Dictionary<string, BoundModule>(StringComparer.Ordinal));
    }

    private static BoundModule CreateImportedModule(
        string alias,
        IReadOnlyList<BoundFunctionMember>? functions = null,
        IReadOnlyDictionary<string, BoundModule>? importedModules = null)
    {
        return new BoundModule(
            $"/tmp/{alias}.blade",
            EmptyCompilationUnit(),
            [],
            [],
            functions ?? [],
            new Dictionary<string, TypeSymbol>(StringComparer.Ordinal),
            (functions ?? []).ToDictionary(member => member.Symbol.Name, member => member.Symbol, StringComparer.Ordinal),
            new Dictionary<string, VariableSymbol>(StringComparer.Ordinal),
            importedModules ?? new Dictionary<string, BoundModule>(StringComparer.Ordinal));
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

    private static BladeValue CreateValue(BladeType type, object value)
    {
        if (type is ArrayTypeSymbol arrayType
            && arrayType.ElementType == BuiltinTypes.U8
            && value is byte[] bytes)
        {
            return BladeValue.U8Array(bytes);
        }

        object canonicalValue = CanonicalizeValue(type, value);
        return type switch
        {
            RuntimeTypeSymbol runtimeType => new RuntimeBladeValue(runtimeType, canonicalValue),
            ComptimeTypeSymbol comptimeType => new ComptimeBladeValue(comptimeType, canonicalValue),
            _ => throw new InvalidOperationException($"Unsupported test literal type '{type.Name}'."),
        };
    }

    private static object CanonicalizeValue(BladeType type, object value)
    {
        if (type is IntegerLiteralTypeSymbol or IntegerTypeSymbol or EnumTypeSymbol or BitfieldTypeSymbol)
        {
            return value switch
            {
                sbyte sbyteValue => (long)sbyteValue,
                byte byteValue => (long)byteValue,
                short shortValue => (long)shortValue,
                ushort ushortValue => (long)ushortValue,
                int intValue => (long)intValue,
                uint uintValue => (long)uintValue,
                long longValue => longValue,
                _ => value,
            };
        }

        return value;
    }

    private static BoundLiteralExpression Literal(object value, BladeType type) => new(CreateValue(type, value), Span);

    private static string GetRecordField(object instance, string propertyName)
    {
        return instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)!.GetValue(instance)!.ToString()!;
    }

    [Test]
    public void StorageLayoutMetadata_RequiresComptimeInteger()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = Bind("extern reg var DIRA: u32 @(true);");

        Assert.That(diagnostics.Any(diagnostic => diagnostic.Code == DiagnosticCode.E0205_TypeMismatch), Is.True);
    }

    [Test]
    public void ImportedPureFunctionCall_FoldsToLiteral()
    {
        using TempDirectory temp = new();
        temp.WriteFile("math.blade", "fn inc(x: u32) -> u32 { return x + 1; }");

        string sourcePath = temp.GetFullPath("main.blade");
        (BoundModule program, IReadOnlyList<Diagnostic> diagnostics) = Bind("""import "./math.blade" as math; var outv: u32 = math.inc(2);""", sourcePath);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        BoundVariableDeclarationStatement statement = (BoundVariableDeclarationStatement)program.TopLevelStatements.Single();
        BoundLiteralExpression initializer = (BoundLiteralExpression)statement.Initializer!;
        Assert.That(initializer.Value.Value, Is.EqualTo(3L));
    }

    [Test]
    public void ComptimeBareIfWithoutElse_FoldsBoolLiteralCondition()
    {
        (BoundModule program, IReadOnlyList<Diagnostic> diagnostics) = Bind("""
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
        Assert.That(initializer.Value.Value, Is.EqualTo(2L));
    }

    [Test]
    public void StaticStorageConstants_AreReadableDuringFoldingAndComptimeEvaluation()
    {
        (BoundModule program, IReadOnlyList<Diagnostic> diagnostics) = Bind("""
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
        Assert.That(program.GlobalVariables.Select(static global => global.Initializer).OfType<BoundLiteralExpression>().Select(static literal => literal.Value.Value), Does.Contain((uint)25));
    }

    [Test]
    public void IntegerLiteralFolding_Keeps64BitIntermediatesUntilFinalMaterialization()
    {
        (BoundModule program, IReadOnlyList<Diagnostic> diagnostics) = Bind("""
            reg const CLOCKS: u32 = 250 * 20_000_000 / 1000;
            """);

        Assert.That(diagnostics.Count, Is.EqualTo(0));

        BoundLiteralExpression initializer = (BoundLiteralExpression)program.GlobalVariables.Single().Initializer!;
        Assert.That(initializer.Value.Value, Is.EqualTo(5_000_000L));
    }

    [Test]
    public void PrivateComptimeValidationHelpers_CoverRecursiveBranches()
    {
        MethodInfo method = GetBinderStaticMethod("TryValidateComptimeExpression", 2);
        VariableSymbol local = new("local", BuiltinTypes.U32, isConst: false, VariableStorageClass.Automatic, VariableScopeKind.Local, isExtern: false, fixedAddress: null, alignment: null);
        BoundExpression localSymbol = new BoundSymbolExpression(local, Span, BuiltinTypes.U32);
        FunctionSymbol function = CreateFunctionSymbol("callme", FunctionKind.Default, BuiltinTypes.U32);
        BoundModule module = CreateImportedModule("mod");
        AggregateMemberSymbol member = new("value", BuiltinTypes.U32, byteOffset: 0, bitOffset: 0, bitWidth: 0, isBitfield: false);
        StructTypeSymbol pairType = new(
            "Pair",
            new Dictionary<string, BladeType>(StringComparer.Ordinal) { ["value"] = BuiltinTypes.U32 },
            new Dictionary<string, AggregateMemberSymbol>(StringComparer.Ordinal) { ["value"] = member },
            sizeBytes: 4,
            alignmentBytes: 4);

        BoundExpression[] unsupportedExpressions =
        [
            new BoundUnaryExpression(BoundUnaryOperator.Bind(TokenKind.Ampersand)!, Literal(1, BuiltinTypes.IntegerLiteral), Span, new PointerTypeSymbol(BuiltinTypes.U32, isConst: false)),
            new BoundBinaryExpression(Literal(1, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Plus)!, localSymbol, Span, BuiltinTypes.IntegerLiteral),
            new BoundArrayLiteralExpression([Literal(1, BuiltinTypes.IntegerLiteral), localSymbol], lastElementIsSpread: false, Span, new ArrayTypeSymbol(BuiltinTypes.U32, 2)),
            new BoundStructLiteralExpression([new BoundStructFieldInitializer("value", localSymbol)], Span, pairType),
            new BoundConversionExpression(localSymbol, Span, BuiltinTypes.U32),
            new BoundCastExpression(localSymbol, Span, BuiltinTypes.U32),
            new BoundBitcastExpression(localSymbol, Span, BuiltinTypes.U32),
            new BoundIfExpression(Literal(true, BuiltinTypes.Bool), Literal(1, BuiltinTypes.IntegerLiteral), localSymbol, Span, BuiltinTypes.IntegerLiteral),
            new BoundCallExpression(function, [], Span, BuiltinTypes.U32),
            new BoundModuleCallExpression(module, Span),
            new BoundIntrinsicCallExpression("encod", [], Span, BuiltinTypes.U32),
            new BoundMemberAccessExpression(new BoundStructLiteralExpression([], Span, pairType), member, Span),
            new BoundIndexExpression(Literal(0, BuiltinTypes.IntegerLiteral), Literal(0, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.U32),
            new BoundPointerDerefExpression(Literal(0u, BuiltinTypes.U32), Span, BuiltinTypes.U32),
            new BoundRangeExpression(Literal(0, BuiltinTypes.IntegerLiteral), localSymbol, false, Span),
            new BoundRangeExpression(Literal(0, BuiltinTypes.IntegerLiteral), localSymbol, true, Span),
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
            new Dictionary<string, BladeType>(StringComparer.Ordinal) { ["value"] = BuiltinTypes.U32 },
            new Dictionary<string, AggregateMemberSymbol>(StringComparer.Ordinal) { ["value"] = new AggregateMemberSymbol("value", BuiltinTypes.U32, 0, 0, 0, isBitfield: false) },
            sizeBytes: 4,
            alignmentBytes: 4);

        Assert.That(Invoke<bool>(method, null, Literal(new byte[] { 116, 101, 120, 116 }, new ArrayTypeSymbol(BuiltinTypes.U8, 4))), Is.True);
        Assert.That(Invoke<bool>(method, null, new BoundUnaryExpression(BoundUnaryOperator.Bind(TokenKind.Minus)!, Literal(1, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.True);
        Assert.That(Invoke<bool>(method, null, new BoundBinaryExpression(Literal(1, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Plus)!, Literal(2, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.True);
        Assert.That(Invoke<bool>(method, null, new BoundCallExpression(function, [], Span, BuiltinTypes.U32)), Is.True);
        Assert.That(Invoke<bool>(method, null, new BoundIfExpression(Literal(true, BuiltinTypes.Bool), Literal(1, BuiltinTypes.IntegerLiteral), Literal(2, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.True);
        Assert.That(Invoke<bool>(method, null, new BoundConversionExpression(Literal(1, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.U32)), Is.True);
        Assert.That(Invoke<bool>(method, null, new BoundConversionExpression(Literal(1, BuiltinTypes.IntegerLiteral), Span, new ArrayTypeSymbol(BuiltinTypes.U8, 1))), Is.False);
        Assert.That(Invoke<bool>(method, null, new BoundCastExpression(Literal(1, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.U8)), Is.True);
        Assert.That(Invoke<bool>(method, null, new BoundCastExpression(Literal(1, BuiltinTypes.IntegerLiteral), Span, unionType)), Is.False);
        Assert.That(Invoke<bool>(method, null, new BoundBitcastExpression(Literal(1, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.I32)), Is.True);
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
                Literal(1, BuiltinTypes.IntegerLiteral),
                BoundBinaryOperator.Bind(TokenKind.EqualEqual)!,
                Literal(1, BuiltinTypes.IntegerLiteral),
                Span,
                BuiltinTypes.Bool),
            null,
        ];
        Assert.That((bool)tryEvaluateConstantValue.Invoke(binder, boolArgs)!, Is.True);
        object comptimeBool = boolArgs[1]!;
        object?[] tryGetBoolArgs = [false];
        Assert.That((bool)tryGetBool.Invoke(comptimeBool, tryGetBoolArgs)!, Is.True);
        Assert.That(tryGetBoolArgs[0], Is.EqualTo(true));

        Assert.That(Invoke(tryEvaluateConstantInt, binder, Literal(7u, BuiltinTypes.U32)), Is.EqualTo(7));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, Literal((short)8, BuiltinTypes.I16)), Is.EqualTo(8));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, Literal((ushort)9, BuiltinTypes.U16)), Is.EqualTo(9));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, Literal((byte)10, BuiltinTypes.U8)), Is.EqualTo(10));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, Literal((sbyte)(-11), BuiltinTypes.I8)), Is.EqualTo(-11));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundConversionExpression(Literal(12, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.U32)), Is.EqualTo(12));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundCastExpression(Literal(255, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.U8)), Is.EqualTo(255));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBitcastExpression(Literal(255, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.I8)), Is.EqualTo(-1));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundUnaryExpression(BoundUnaryOperator.Bind(TokenKind.Minus)!, Literal(5, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(-5));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundUnaryExpression(BoundUnaryOperator.Bind(TokenKind.Tilde)!, Literal(5, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(~5));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundUnaryExpression(BoundUnaryOperator.Bind(TokenKind.Plus)!, Literal(5, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(5));

        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(Literal(7, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Plus)!, Literal(2, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(9));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(Literal(7, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Minus)!, Literal(2, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(5));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(Literal(7, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Star)!, Literal(2, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(14));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(Literal(7, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Slash)!, Literal(2, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(3));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(Literal(7, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Slash)!, Literal(0, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.Null);
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(Literal(7, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Percent)!, Literal(2, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(1));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(Literal(7, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Percent)!, Literal(0, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.Null);
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(Literal(6, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Ampersand)!, Literal(3, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(2));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(Literal(6, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Pipe)!, Literal(3, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(7));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(Literal(6, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Caret)!, Literal(3, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(5));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(Literal(3, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.LessLess)!, Literal(2, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(12));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(Literal(8, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.GreaterGreater)!, Literal(1, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(4));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(Literal(3, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.LessLessLess)!, Literal(1, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(6));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(Literal(8, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.GreaterGreaterGreater)!, Literal(1, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(4));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(Literal(1, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.RotateLeft)!, Literal(1, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(2));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(Literal(2, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.RotateRight)!, Literal(1, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(1));
        Assert.That(Invoke(tryEvaluateConstantInt, binder, new BoundBinaryExpression(Literal(1, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.EqualEqual)!, Literal(1, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.Bool)), Is.Null);

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
        Assert.That(Invoke(tryEvaluateConstantInt, binder, Literal((long)int.MaxValue + 1L, BuiltinTypes.IntegerLiteral)), Is.Null);
    }

    [Test]
    public void PrivateComptimeResolutionAndReportingHelpers_CoverImportedLookupAndDiagnostics()
    {
        DiagnosticBag diagnostics = new();
        SemanticBinder binder = CreateBinder(diagnostics);
        FieldInfo boundBodiesField = typeof(SemanticBinder).GetField("_boundFunctionBodies", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo importedModulesField = typeof(SemanticBinder).GetField("_importedModules", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Dictionary<FunctionSymbol, BoundBlockStatement> boundBodies = (Dictionary<FunctionSymbol, BoundBlockStatement>)boundBodiesField.GetValue(binder)!;
        Dictionary<string, BoundModule> importedModules = (Dictionary<string, BoundModule>)importedModulesField.GetValue(binder)!;
        MethodInfo resolveBody = GetBinderInstanceMethod("ResolveFunctionBodyForComptime", typeof(FunctionSymbol));
        MethodInfo reportFailure = GetBinderInstanceMethod("ReportComptimeFailure", CreateFailure("NotEvaluable").GetType());

        FunctionSymbol localFunction = CreateFunctionSymbol("local", FunctionKind.Default);
        BoundBlockStatement localBody = new([], Span);
        boundBodies.Add(localFunction, localBody);
        Assert.That(Invoke(resolveBody, binder, localFunction), Is.SameAs(localBody));

        FunctionSymbol importedFunction = CreateFunctionSymbol("imported", FunctionKind.Default);
        BoundBlockStatement importedBody = new([], Span);
        BoundModule nestedModule = CreateImportedModule("nested", [new BoundFunctionMember(importedFunction, importedBody, Span)]);
        BoundModule rootModule = CreateImportedModule(
            "root",
            functions: [],
            importedModules: new Dictionary<string, BoundModule>(StringComparer.Ordinal) { ["nested"] = nestedModule });
        importedModules.Add("root", rootModule);

        Assert.That(Invoke(resolveBody, binder, importedFunction), Is.SameAs(importedBody));

        using (diagnostics.UseSource(new SourceText(string.Empty, "<input>")))
        {
            Invoke(reportFailure, binder, CreateFailure("NotEvaluable"));
            Invoke(reportFailure, binder, CreateFailure("UnsupportedConstruct", "unsupported"));
            Invoke(reportFailure, binder, CreateFailure("ForbiddenSymbolAccess", "forbidden"));
            Invoke(reportFailure, binder, CreateFailure("FuelExhausted"));
        }

        Assert.That(diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(
        [
            DiagnosticCode.E0241_ComptimeValueRequired,
            DiagnosticCode.E0242_ComptimeUnsupportedConstruct,
            DiagnosticCode.E0243_ComptimeForbiddenSymbolAccess,
            DiagnosticCode.E0244_ComptimeFuelExhausted,
        ]));
    }
}
