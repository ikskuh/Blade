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
        return (SemanticBinder)constructor.Invoke([diagnostics, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 250]);
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

    private static MethodInfo GetBinderInstanceMethod(string name, int parameterCount)
    {
        return typeof(SemanticBinder).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == name && method.GetParameters().Length == parameterCount);
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
            ReturnTypes = returnTypes,
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
    public void PrivateComptimeValidationHelpers_CoverRecursiveBranches()
    {
        MethodInfo method = GetBinderStaticMethod("TryValidateComptimeExpression", 2);
        VariableSymbol local = new("local", BuiltinTypes.U32, isConst: false, VariableStorageClass.Automatic, VariableScopeKind.Local, isExtern: false, fixedAddress: null, alignment: null);
        BoundExpression localSymbol = new BoundSymbolExpression(local, Span, BuiltinTypes.U32);
        FunctionSymbol function = CreateFunctionSymbol("callme", FunctionKind.Default, BuiltinTypes.U32);
        ImportedModule module = CreateImportedModule("mod");
        AggregateMemberSymbol member = new("value", BuiltinTypes.U32, byteOffset: 0, bitOffset: 0, bitWidth: 0, isBitfield: false);
        StructTypeSymbol pairType = new("Pair", new Dictionary<string, TypeSymbol>(StringComparer.Ordinal) { ["value"] = BuiltinTypes.U32 });

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
            new BoundRangeExpression(new BoundLiteralExpression(0, Span, BuiltinTypes.IntegerLiteral), localSymbol, Span),
            new BoundErrorExpression(Span),
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
    public void PrivateContainsErrorExpression_CoversRecursiveShapes()
    {
        MethodInfo method = GetBinderStaticMethod("ContainsErrorExpression", typeof(BoundExpression));
        BoundErrorExpression error = new(Span);
        StructTypeSymbol pairType = new("Pair", new Dictionary<string, TypeSymbol>(StringComparer.Ordinal) { ["value"] = BuiltinTypes.U32 });
        FunctionSymbol function = CreateFunctionSymbol("callme", FunctionKind.Default, BuiltinTypes.U32);

        BoundExpression[] expressionsWithErrors =
        [
            new BoundCallExpression(function, [error], Span, BuiltinTypes.U32),
            new BoundIntrinsicCallExpression("encod", [error], Span, BuiltinTypes.U32),
            new BoundArrayLiteralExpression([error], lastElementIsSpread: false, Span, new ArrayTypeSymbol(BuiltinTypes.U32, 1)),
            new BoundStructLiteralExpression([new BoundStructFieldInitializer("value", error)], Span, pairType),
            new BoundConversionExpression(error, Span, BuiltinTypes.U32),
            new BoundCastExpression(error, Span, BuiltinTypes.U32),
            new BoundBitcastExpression(error, Span, BuiltinTypes.U32),
            new BoundIfExpression(new BoundLiteralExpression(true, Span, BuiltinTypes.Bool), error, new BoundLiteralExpression(0, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral),
            new BoundRangeExpression(new BoundLiteralExpression(0, Span, BuiltinTypes.IntegerLiteral), error, Span),
        ];

        foreach (BoundExpression expression in expressionsWithErrors)
            Assert.That(Invoke<bool>(method, null, expression), Is.True);

        Assert.That(Invoke<bool>(method, null, new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral)), Is.False);
        Assert.That(Invoke<bool>(method, null, new BoundIntrinsicCallExpression("encod", [], Span, BuiltinTypes.U32)), Is.False);
        Assert.That(Invoke<bool>(method, null, new BoundIntrinsicCallExpression("encod", [new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral)], Span, BuiltinTypes.U32)), Is.False);
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
        Assert.That(Invoke<bool>(method, null, new BoundEnumLiteralExpression(mode, "On", 1, Span)), Is.True);
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
        MethodInfo tryEvaluateConstantInt = GetBinderStaticMethod("TryEvaluateConstantInt", typeof(BoundExpression));
        MethodInfo toInt32Unchecked = GetBinderStaticMethod("ToInt32Unchecked", typeof(object));

        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundLiteralExpression((ulong)7, Span, BuiltinTypes.U32)), Is.EqualTo(7));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundLiteralExpression((short)8, Span, BuiltinTypes.I16)), Is.EqualTo(8));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundLiteralExpression((ushort)9, Span, BuiltinTypes.U16)), Is.EqualTo(9));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundLiteralExpression((byte)10, Span, BuiltinTypes.U8)), Is.EqualTo(10));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundLiteralExpression((sbyte)(-11), Span, BuiltinTypes.I8)), Is.EqualTo(-11));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundConversionExpression(new BoundLiteralExpression(12, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.U32)), Is.EqualTo(12));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundCastExpression(new BoundLiteralExpression(255, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.U8)), Is.EqualTo(255));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundBitcastExpression(new BoundLiteralExpression(255, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.I8)), Is.EqualTo(-1));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundUnaryExpression(BoundUnaryOperator.Bind(TokenKind.Minus)!, new BoundLiteralExpression(5, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(-5));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundUnaryExpression(BoundUnaryOperator.Bind(TokenKind.Tilde)!, new BoundLiteralExpression(5, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(~5));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundUnaryExpression(BoundUnaryOperator.Bind(TokenKind.Plus)!, new BoundLiteralExpression(5, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(5));

        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundBinaryExpression(new BoundLiteralExpression(7, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Plus)!, new BoundLiteralExpression(2, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(9));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundBinaryExpression(new BoundLiteralExpression(7, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Minus)!, new BoundLiteralExpression(2, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(5));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundBinaryExpression(new BoundLiteralExpression(7, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Star)!, new BoundLiteralExpression(2, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(14));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundBinaryExpression(new BoundLiteralExpression(7, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Slash)!, new BoundLiteralExpression(2, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(3));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundBinaryExpression(new BoundLiteralExpression(7, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Slash)!, new BoundLiteralExpression(0, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.Null);
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundBinaryExpression(new BoundLiteralExpression(7, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Percent)!, new BoundLiteralExpression(2, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(1));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundBinaryExpression(new BoundLiteralExpression(7, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Percent)!, new BoundLiteralExpression(0, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.Null);
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundBinaryExpression(new BoundLiteralExpression(6, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Ampersand)!, new BoundLiteralExpression(3, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(2));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundBinaryExpression(new BoundLiteralExpression(6, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Pipe)!, new BoundLiteralExpression(3, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(7));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundBinaryExpression(new BoundLiteralExpression(6, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.Caret)!, new BoundLiteralExpression(3, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(5));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundBinaryExpression(new BoundLiteralExpression(3, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.LessLess)!, new BoundLiteralExpression(2, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(12));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundBinaryExpression(new BoundLiteralExpression(8, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.GreaterGreater)!, new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(4));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundBinaryExpression(new BoundLiteralExpression(3, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.LessLessLess)!, new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(6));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundBinaryExpression(new BoundLiteralExpression(8, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.GreaterGreaterGreater)!, new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(4));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundBinaryExpression(new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.RotateLeft)!, new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(2));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundBinaryExpression(new BoundLiteralExpression(2, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.RotateRight)!, new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.IntegerLiteral)), Is.EqualTo(1));
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundBinaryExpression(new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), BoundBinaryOperator.Bind(TokenKind.EqualEqual)!, new BoundLiteralExpression(1, Span, BuiltinTypes.IntegerLiteral), Span, BuiltinTypes.Bool)), Is.Null);
        Assert.That(Invoke(tryEvaluateConstantInt, null, new BoundErrorExpression(Span)), Is.Null);

        Assert.That(Invoke<int>(toInt32Unchecked, null, (object?)null), Is.EqualTo(0));
        Assert.That(Invoke<int>(toInt32Unchecked, null, 1), Is.EqualTo(1));
        Assert.That(Invoke<int>(toInt32Unchecked, null, (uint)2), Is.EqualTo(2));
        Assert.That(Invoke<int>(toInt32Unchecked, null, (long)3), Is.EqualTo(3));
        Assert.That(Invoke<int>(toInt32Unchecked, null, (ulong)4), Is.EqualTo(4));
        Assert.That(Invoke<int>(toInt32Unchecked, null, (short)5), Is.EqualTo(5));
        Assert.That(Invoke<int>(toInt32Unchecked, null, (ushort)6), Is.EqualTo(6));
        Assert.That(Invoke<int>(toInt32Unchecked, null, (byte)7), Is.EqualTo(7));
        Assert.That(Invoke<int>(toInt32Unchecked, null, (sbyte)8), Is.EqualTo(8));
        Assert.That(Invoke<int>(toInt32Unchecked, null, 9m), Is.EqualTo(9));
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
        MethodInfo resolveBody = GetBinderInstanceMethod("ResolveFunctionBodyForComptime", 1);
        MethodInfo getSupport = GetBinderInstanceMethod("GetComptimeSupportResult", 1);
        MethodInfo reportFailure = GetBinderInstanceMethod("ReportComptimeFailure", 1);

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

        FunctionSymbol missingFunction = CreateFunctionSymbol("missing", FunctionKind.Default);
        object missingSupport = Invoke(getSupport, binder, missingFunction)!;
        PropertyInfo isSupportedProperty = missingSupport.GetType().GetProperty("IsSupported")!;
        Assert.That((bool)isSupportedProperty.GetValue(missingSupport)!, Is.False);

        Invoke(reportFailure, binder, CreateFailure("None"));
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
