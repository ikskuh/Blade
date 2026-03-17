using System.Reflection;
using Blade.Diagnostics;
using Blade.Source;
using Blade;
using Blade.Syntax;
using Blade.Syntax.Nodes;

namespace Blade.Tests;

[TestFixture]
public class ParserTests
{
    private static (CompilationUnitSyntax Unit, DiagnosticBag Diagnostics) Parse(string text)
    {
        SourceText source = new(text);
        DiagnosticBag diagnostics = new();
        Parser parser = Parser.Create(source, diagnostics);
        CompilationUnitSyntax unit = parser.ParseCompilationUnit();
        return (unit, diagnostics);
    }

    private static Parser CreateParser(params Token[] tokens)
    {
        SourceText source = new(string.Empty);
        DiagnosticBag diagnostics = new();
        return new Parser(source, tokens, diagnostics);
    }

    private static void AssertNoDiagnostics(DiagnosticBag diagnostics)
    {
        if (diagnostics.Count > 0)
        {
            string errors = string.Join("\n", diagnostics.Select(d => d.ToString()));
            Assert.Fail($"Expected no diagnostics but got:\n{errors}");
        }
    }

    // ── Smoke tests ──

    [Test]
    public void EmptyProgram_ReturnsEmptyCompilationUnit()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("");
        AssertNoDiagnostics(diag);
        Assert.That(unit.Members, Is.Empty);
    }

    [Test]
    public void SingleVariableDeclaration_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("reg var x: u32 = 42;");
        AssertNoDiagnostics(diag);
        Assert.That(unit.Members, Has.Count.EqualTo(1));
        Assert.That(unit.Members[0], Is.TypeOf<VariableDeclarationSyntax>());

        VariableDeclarationSyntax decl = (VariableDeclarationSyntax)unit.Members[0];
        Assert.That(decl.StorageClassKeyword, Is.Not.Null);
        Assert.That(decl.StorageClassKeyword is Token storageClass && storageClass.Kind == TokenKind.RegKeyword, Is.True);
        Assert.That(decl.MutabilityKeyword.Kind, Is.EqualTo(TokenKind.VarKeyword));
        Assert.That(decl.Name.Text, Is.EqualTo("x"));
        Assert.That(decl.Type, Is.TypeOf<PrimitiveTypeSyntax>());
        Assert.That(decl.Initializer, Is.TypeOf<LiteralExpressionSyntax>());
    }

    [Test]
    public void SimpleFunctionDeclaration_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("fn add(a: u32, b: u32) -> u32 { return a + b; }");
        AssertNoDiagnostics(diag);
        Assert.That(unit.Members, Has.Count.EqualTo(1));
        Assert.That(unit.Members[0], Is.TypeOf<FunctionDeclarationSyntax>());

        FunctionDeclarationSyntax func = (FunctionDeclarationSyntax)unit.Members[0];
        Assert.That(func.Name.Text, Is.EqualTo("add"));
        Assert.That(func.Parameters.Count, Is.EqualTo(2));
        Assert.That(func.ReturnSpec, Is.Not.Null);
        Assert.That(func.ReturnSpec!.Count, Is.EqualTo(1));
    }

    // ── Expression precedence ──

    [Test]
    public void MultiplicationBindsTighterThanAddition()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("reg var x: u32 = 1 + 2 * 3;");
        AssertNoDiagnostics(diag);

        VariableDeclarationSyntax decl = (VariableDeclarationSyntax)unit.Members[0];
        BinaryExpressionSyntax binary = (BinaryExpressionSyntax)decl.Initializer!;

        // Should be (1 + (2 * 3)), so top-level is +
        Assert.That(binary.Operator.Kind, Is.EqualTo(TokenKind.Plus));
        Assert.That(binary.Right, Is.TypeOf<BinaryExpressionSyntax>());
        Assert.That(((BinaryExpressionSyntax)binary.Right).Operator.Kind, Is.EqualTo(TokenKind.Star));
    }

    [Test]
    public void UnaryMinusBindsTighterThanBinary()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("reg var x: u32 = -1 + 2;");
        AssertNoDiagnostics(diag);

        VariableDeclarationSyntax decl = (VariableDeclarationSyntax)unit.Members[0];
        BinaryExpressionSyntax binary = (BinaryExpressionSyntax)decl.Initializer!;

        Assert.That(binary.Operator.Kind, Is.EqualTo(TokenKind.Plus));
        Assert.That(binary.Left, Is.TypeOf<UnaryExpressionSyntax>());
    }

    [Test]
    public void ParenthesesOverridePrecedence()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("reg var x: u32 = (1 + 2) * 3;");
        AssertNoDiagnostics(diag);

        VariableDeclarationSyntax decl = (VariableDeclarationSyntax)unit.Members[0];
        BinaryExpressionSyntax binary = (BinaryExpressionSyntax)decl.Initializer!;

        // Should be ((1 + 2) * 3), so top-level is *
        Assert.That(binary.Operator.Kind, Is.EqualTo(TokenKind.Star));
        Assert.That(binary.Left, Is.TypeOf<ParenthesizedExpressionSyntax>());
    }

    [Test]
    public void InvalidQuaternaryLiteral_DoesNotCascadeIntoUnexpectedToken()
    {
        (_, DiagnosticBag diag) = Parse("reg var x: u32 = 0q123_456;");

        Assert.That(diag.Select(diagnostic => diagnostic.Code), Is.EqualTo([DiagnosticCode.E0003_InvalidNumberLiteral]));
    }

    // ── Statements ──

    [Test]
    public void IfElseStatement_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("if (x) { y = 1; } else { y = 2; }");
        AssertNoDiagnostics(diag);

        GlobalStatementSyntax global = (GlobalStatementSyntax)unit.Members[0];
        IfStatementSyntax ifStmt = (IfStatementSyntax)global.Statement;
        Assert.That(ifStmt.ElseClause, Is.Not.Null);
        Assert.That(ifStmt.ThenBody, Is.TypeOf<BlockStatementSyntax>());
        Assert.That(ifStmt.ElseClause!.Body, Is.TypeOf<BlockStatementSyntax>());
    }

    [Test]
    public void WhileLoop_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("while (x != 0) { x = x - 1; }");
        AssertNoDiagnostics(diag);

        GlobalStatementSyntax global = (GlobalStatementSyntax)unit.Members[0];
        Assert.That(global.Statement, Is.TypeOf<WhileStatementSyntax>());
    }

    [Test]
    public void AssignmentStatement_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("x = 42;");
        AssertNoDiagnostics(diag);

        GlobalStatementSyntax global = (GlobalStatementSyntax)unit.Members[0];
        AssignmentStatementSyntax assign = (AssignmentStatementSyntax)global.Statement;
        Assert.That(assign.Operator.Kind, Is.EqualTo(TokenKind.Equal));
        Assert.That(assign.Target, Is.TypeOf<NameExpressionSyntax>());
    }

    [Test]
    public void CompoundAssignment_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("x += 1;");
        AssertNoDiagnostics(diag);

        GlobalStatementSyntax global = (GlobalStatementSyntax)unit.Members[0];
        AssignmentStatementSyntax assign = (AssignmentStatementSyntax)global.Statement;
        Assert.That(assign.Operator.Kind, Is.EqualTo(TokenKind.PlusEqual));
    }

    [Test]
    public void AsmVolatileStatement_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("""
            asm volatile {
                MOV x, y
            };
            """);
        AssertNoDiagnostics(diag);

        GlobalStatementSyntax global = (GlobalStatementSyntax)unit.Members[0];
        AsmBlockStatementSyntax asm = (AsmBlockStatementSyntax)global.Statement;
        Assert.That(asm.VolatileKeyword, Is.Not.Null);
        Assert.That(asm.VolatileKeyword!.Value.Kind, Is.EqualTo(TokenKind.VolatileKeyword));
        Assert.That(asm.Volatility, Is.EqualTo(AsmVolatility.Volatile));
    }

    // ── Types ──

    [Test]
    public void ArrayType_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("reg var buf: [32]u8 = undefined;");
        AssertNoDiagnostics(diag);

        VariableDeclarationSyntax decl = (VariableDeclarationSyntax)unit.Members[0];
        Assert.That(decl.Type, Is.TypeOf<ArrayTypeSyntax>());
        ArrayTypeSyntax arr = (ArrayTypeSyntax)decl.Type;
        Assert.That(arr.ElementType, Is.TypeOf<PrimitiveTypeSyntax>());
    }

    [Test]
    public void PointerType_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("reg var p: *const u8 = undefined;");
        AssertNoDiagnostics(diag);

        VariableDeclarationSyntax decl = (VariableDeclarationSyntax)unit.Members[0];
        Assert.That(decl.Type, Is.TypeOf<PointerTypeSyntax>());
        PointerTypeSyntax ptr = (PointerTypeSyntax)decl.Type;
        Assert.That(ptr.ConstKeyword, Is.Not.Null);
    }

    [Test]
    public void GenericWidthType_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("reg var x: uint(5) = 0;");
        AssertNoDiagnostics(diag);

        VariableDeclarationSyntax decl = (VariableDeclarationSyntax)unit.Members[0];
        Assert.That(decl.Type, Is.TypeOf<GenericWidthTypeSyntax>());
    }

    // ── Error recovery ──

    [Test]
    public void MissingSemicolon_ReportsDiagnostic()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("reg var x: u32 = 0");
        Assert.That(diag.Count, Is.GreaterThan(0));
        Assert.That(diag.Any(d => d.Code == DiagnosticCode.E0101_UnexpectedToken), Is.True);
    }

    [Test]
    public void ImportDeclaration_FileImport_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("import \"math\" as math;");
        AssertNoDiagnostics(diag);

        Assert.That(unit.Members[0], Is.TypeOf<ImportDeclarationSyntax>());
        ImportDeclarationSyntax import = (ImportDeclarationSyntax)unit.Members[0];
        Assert.That(import.IsFileImport, Is.True);
        Assert.That(import.Source.Value, Is.EqualTo("math"));
        Assert.That(import.Alias!.Value.Text, Is.EqualTo("math"));
    }

    [Test]
    public void ImportDeclaration_NamedModule_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("import extmod;");
        AssertNoDiagnostics(diag);

        Assert.That(unit.Members[0], Is.TypeOf<ImportDeclarationSyntax>());
        ImportDeclarationSyntax import = (ImportDeclarationSyntax)unit.Members[0];
        Assert.That(import.IsFileImport, Is.False);
        Assert.That(import.Source.Text, Is.EqualTo("extmod"));
        Assert.That(import.AsKeyword, Is.Null);
        Assert.That(import.Alias, Is.Null);
    }

    [Test]
    public void ImportDeclaration_NamedModuleWithAlias_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("import extmod as ext;");
        AssertNoDiagnostics(diag);

        Assert.That(unit.Members[0], Is.TypeOf<ImportDeclarationSyntax>());
        ImportDeclarationSyntax import = (ImportDeclarationSyntax)unit.Members[0];
        Assert.That(import.IsFileImport, Is.False);
        Assert.That(import.Source.Text, Is.EqualTo("extmod"));
        Assert.That(import.Alias!.Value.Text, Is.EqualTo("ext"));
    }

    [Test]
    public void ExternConstVariableDeclaration_ParsesAllOptionalClauses()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("extern reg const table: u32 @(1) align(4) = 7;");
        AssertNoDiagnostics(diag);

        VariableDeclarationSyntax decl = (VariableDeclarationSyntax)unit.Members[0];
        Assert.That(decl.ExternKeyword, Is.Not.Null);
        Assert.That(decl.MutabilityKeyword.Kind, Is.EqualTo(TokenKind.ConstKeyword));
        Assert.That(decl.AtClause, Is.Not.Null);
        Assert.That(decl.AlignClause, Is.Not.Null);
        Assert.That(decl.Initializer, Is.TypeOf<LiteralExpressionSyntax>());
    }

    [Test]
    public void MissingVariableMutability_ReportsDiagnostic()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("reg table: u32;");
        Assert.That(unit.Members[0], Is.TypeOf<VariableDeclarationSyntax>());
        Assert.That(diag.Any(d => d.Code == DiagnosticCode.E0101_UnexpectedToken), Is.True);
    }

    [Test]
    public void PackedStructTypeAlias_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("const Header = packed struct { lo: u8, hi: u8 };");
        AssertNoDiagnostics(diag);

        TypeAliasDeclarationSyntax alias = (TypeAliasDeclarationSyntax)unit.Members[0];
        Assert.That(alias.Type, Is.TypeOf<StructTypeSyntax>());
        StructTypeSyntax type = (StructTypeSyntax)alias.Type;
        Assert.That(type.Fields.Count, Is.EqualTo(2));
    }

    [Test]
    public void ComptimeConstDeclaration_ParsesAsTypeAliasPlaceholder()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("const BuildInfo = comptime { 1; };");
        AssertNoDiagnostics(diag);

        TypeAliasDeclarationSyntax alias = (TypeAliasDeclarationSyntax)unit.Members[0];
        Assert.That(alias.Type, Is.TypeOf<NamedTypeSyntax>());
        Assert.That(((NamedTypeSyntax)alias.Type).Name.Text, Is.EqualTo("auto"));
    }

    [Test]
    public void ComptimeFunctionDeclaration_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("comptime fn init() { return; }");
        AssertNoDiagnostics(diag);

        FunctionDeclarationSyntax func = (FunctionDeclarationSyntax)unit.Members[0];
        Assert.That(func.FuncKindKeyword?.Kind, Is.EqualTo(TokenKind.ComptimeKeyword));
        Assert.That(func.Name.Text, Is.EqualTo("init"));
    }

    [Test]
    public void FunctionWithBareReturnType_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("fn nop() void { return; }");
        AssertNoDiagnostics(diag);

        FunctionDeclarationSyntax func = (FunctionDeclarationSyntax)unit.Members[0];
        Assert.That(func.Arrow, Is.Null);
        Assert.That(func.ReturnSpec, Is.Not.Null);
        Assert.That(func.ReturnSpec![0].Type, Is.TypeOf<PrimitiveTypeSyntax>());
    }

    [TestCase("bool")]
    [TestCase("bit")]
    [TestCase("nit")]
    [TestCase("nib")]
    [TestCase("u8")]
    [TestCase("i8")]
    [TestCase("u16")]
    [TestCase("i16")]
    [TestCase("u32")]
    [TestCase("i32")]
    [TestCase("uint(5)")]
    [TestCase("int(5)")]
    [TestCase("*const u8")]
    [TestCase("[4]u8")]
    [TestCase("CustomType")]
    public void FunctionWithBareReturnTypeStart_ParsesCorrectly(string returnType)
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse($"fn f() {returnType} {{ return; }}");
        AssertNoDiagnostics(diag);

        FunctionDeclarationSyntax func = (FunctionDeclarationSyntax)unit.Members[0];
        Assert.That(func.ReturnSpec, Is.Not.Null);
        Assert.That(func.ReturnSpec!.Count, Is.EqualTo(1));
    }

    [Test]
    public void FunctionWithBarePackedStructReturnType_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("fn f() packed struct { lo: u8, hi: u8 } { return; }");
        AssertNoDiagnostics(diag);

        FunctionDeclarationSyntax func = (FunctionDeclarationSyntax)unit.Members[0];
        Assert.That(func.ReturnSpec![0].Type, Is.TypeOf<StructTypeSyntax>());
    }

    [Test]
    public void FunctionWithNamedAndFlaggedReturnItems_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("fn status() -> value: u32 @C, flag: bool { return 1, true; }");
        AssertNoDiagnostics(diag);

        FunctionDeclarationSyntax func = (FunctionDeclarationSyntax)unit.Members[0];
        Assert.That(func.ReturnSpec, Is.Not.Null);
        Assert.That(func.ReturnSpec!.Count, Is.EqualTo(2));
        Assert.That(func.ReturnSpec[0].Name?.Text, Is.EqualTo("value"));
        Assert.That(func.ReturnSpec[0].FlagAnnotation?.Flag.Text, Is.EqualTo("C"));
        Assert.That(func.ReturnSpec[1].Name?.Text, Is.EqualTo("flag"));
        Assert.That(func.ReturnSpec[1].FlagAnnotation, Is.Null);
    }

    [Test]
    public void IfElseIfStatement_ParsesNestedNonBlockBodies()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("if (x) y = 1; else if (z) y = 2;");
        AssertNoDiagnostics(diag);

        GlobalStatementSyntax global = (GlobalStatementSyntax)unit.Members[0];
        IfStatementSyntax ifStmt = (IfStatementSyntax)global.Statement;
        Assert.That(ifStmt.ThenBody, Is.TypeOf<AssignmentStatementSyntax>());
        Assert.That(ifStmt.ElseClause?.Body, Is.TypeOf<IfStatementSyntax>());
    }

    [Test]
    public void IfElseStatement_WithPlainElseBody_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("if (x) { y = 1; } else y = 2;");
        AssertNoDiagnostics(diag);

        GlobalStatementSyntax global = (GlobalStatementSyntax)unit.Members[0];
        IfStatementSyntax ifStmt = (IfStatementSyntax)global.Statement;
        Assert.That(ifStmt.ElseClause?.Body, Is.TypeOf<AssignmentStatementSyntax>());
    }

    [Test]
    public void StatementForms_ParsesControlFlowVariants()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("""
            {
                extern reg var ext: u32;
                for (i) { }
                loop { }
                rep loop (4) { }
                rep for (4) { }
                noirq { }
                break;
                continue;
                yield;
                yieldto worker(a, b);
                return;
                return a, b;
                asm { { } } -> state: bool@C;
            }
            """);
        AssertNoDiagnostics(diag);

        GlobalStatementSyntax global = (GlobalStatementSyntax)unit.Members[0];
        BlockStatementSyntax block = (BlockStatementSyntax)global.Statement;
        Assert.That(block.Statements.Count, Is.EqualTo(13));
        Assert.That(block.Statements[0], Is.TypeOf<VariableDeclarationStatementSyntax>());
        Assert.That(block.Statements[1], Is.TypeOf<ForStatementSyntax>());
        Assert.That(block.Statements[2], Is.TypeOf<LoopStatementSyntax>());
        Assert.That(block.Statements[3], Is.TypeOf<RepLoopStatementSyntax>());
        Assert.That(block.Statements[4], Is.TypeOf<RepForStatementSyntax>());
        Assert.That(block.Statements[5], Is.TypeOf<NoirqStatementSyntax>());
        Assert.That(block.Statements[6], Is.TypeOf<BreakStatementSyntax>());
        Assert.That(block.Statements[7], Is.TypeOf<ContinueStatementSyntax>());
        Assert.That(block.Statements[8], Is.TypeOf<YieldStatementSyntax>());
        Assert.That(block.Statements[9], Is.TypeOf<YieldtoStatementSyntax>());
        Assert.That(block.Statements[10], Is.TypeOf<ReturnStatementSyntax>());
        Assert.That(((ReturnStatementSyntax)block.Statements[10]).Values, Is.Null);
        Assert.That(block.Statements[11], Is.TypeOf<ReturnStatementSyntax>());
        Assert.That(((ReturnStatementSyntax)block.Statements[11]).Values?.Count, Is.EqualTo(2));
        Assert.That(block.Statements[12], Is.TypeOf<AsmBlockStatementSyntax>());
        Assert.That(((AsmBlockStatementSyntax)block.Statements[12]).OutputBinding?.FlagAnnotation?.Flag.Text, Is.EqualTo("C"));
    }

    [Test]
    public void LocalConstDeclaration_ParsesAsVariableDeclarationStatement()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("""
            fn demo(param: u32) void {
                const x: u32 = param * 2;
            }
            """);
        AssertNoDiagnostics(diag);

        FunctionDeclarationSyntax function = (FunctionDeclarationSyntax)unit.Members[0];
        VariableDeclarationStatementSyntax statement = (VariableDeclarationStatementSyntax)function.Body.Statements[0];
        VariableDeclarationSyntax declaration = statement.Declaration;

        Assert.That(declaration.MutabilityKeyword.Kind, Is.EqualTo(TokenKind.ConstKeyword));
        Assert.That(declaration.Name.Text, Is.EqualTo("x"));
        Assert.That(declaration.Initializer, Is.TypeOf<BinaryExpressionSyntax>());
    }

    [Test]
    public void RangeExpression_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("reg var span: u32 = 1..4;");
        AssertNoDiagnostics(diag);

        VariableDeclarationSyntax decl = (VariableDeclarationSyntax)unit.Members[0];
        Assert.That(decl.Initializer, Is.TypeOf<RangeExpressionSyntax>());
    }

    [Test]
    public void PointerDerefExpression_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("ptr.*;");
        AssertNoDiagnostics(diag);

        GlobalStatementSyntax global = (GlobalStatementSyntax)unit.Members[0];
        ExpressionStatementSyntax statement = (ExpressionStatementSyntax)global.Statement;
        Assert.That(statement.Expression, Is.TypeOf<PointerDerefExpressionSyntax>());
    }

    [Test]
    public void MemberAccessExpression_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("node.value;");
        AssertNoDiagnostics(diag);

        GlobalStatementSyntax global = (GlobalStatementSyntax)unit.Members[0];
        ExpressionStatementSyntax statement = (ExpressionStatementSyntax)global.Statement;
        Assert.That(statement.Expression, Is.TypeOf<MemberAccessExpressionSyntax>());
    }

    [Test]
    public void CallIndexAndPostfixExpressions_ParseCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("""
            {
                items[0];
                invoke(a, b);
                counter++;
                other--;
            }
            """);
        AssertNoDiagnostics(diag);

        BlockStatementSyntax block = (BlockStatementSyntax)((GlobalStatementSyntax)unit.Members[0]).Statement;
        Assert.That(((ExpressionStatementSyntax)block.Statements[0]).Expression, Is.TypeOf<IndexExpressionSyntax>());
        Assert.That(((ExpressionStatementSyntax)block.Statements[1]).Expression, Is.TypeOf<CallExpressionSyntax>());
        Assert.That(((ExpressionStatementSyntax)block.Statements[2]).Expression, Is.TypeOf<PostfixUnaryExpressionSyntax>());
        Assert.That(((ExpressionStatementSyntax)block.Statements[3]).Expression, Is.TypeOf<PostfixUnaryExpressionSyntax>());
    }

    [Test]
    public void CallExpression_WithNamedArguments_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("invoke(x=10, y=20);");
        AssertNoDiagnostics(diag);

        GlobalStatementSyntax global = (GlobalStatementSyntax)unit.Members[0];
        ExpressionStatementSyntax statement = (ExpressionStatementSyntax)global.Statement;
        CallExpressionSyntax call = (CallExpressionSyntax)statement.Expression;

        Assert.That(call.Arguments[0], Is.TypeOf<NamedArgumentSyntax>());
        Assert.That(call.Arguments[1], Is.TypeOf<NamedArgumentSyntax>());
    }

    [Test]
    public void CastExpression_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("value as u8;");
        AssertNoDiagnostics(diag);

        GlobalStatementSyntax global = (GlobalStatementSyntax)unit.Members[0];
        ExpressionStatementSyntax statement = (ExpressionStatementSyntax)global.Statement;
        CastExpressionSyntax cast = (CastExpressionSyntax)statement.Expression;

        Assert.That(cast.Expression, Is.TypeOf<NameExpressionSyntax>());
        Assert.That(cast.TargetType, Is.TypeOf<PrimitiveTypeSyntax>());
    }

    [Test]
    public void BitcastExpression_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("bitcast(*reg u32, value);");
        AssertNoDiagnostics(diag);

        GlobalStatementSyntax global = (GlobalStatementSyntax)unit.Members[0];
        ExpressionStatementSyntax statement = (ExpressionStatementSyntax)global.Statement;
        BitcastExpressionSyntax bitcast = (BitcastExpressionSyntax)statement.Expression;

        Assert.That(bitcast.Value, Is.TypeOf<NameExpressionSyntax>());
        Assert.That(bitcast.TargetType, Is.TypeOf<PointerTypeSyntax>());
    }

    [Test]
    public void IntrinsicCallExpression_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("@encod(value);");
        AssertNoDiagnostics(diag);

        GlobalStatementSyntax global = (GlobalStatementSyntax)unit.Members[0];
        ExpressionStatementSyntax statement = (ExpressionStatementSyntax)global.Statement;
        Assert.That(statement.Expression, Is.TypeOf<IntrinsicCallExpressionSyntax>());
    }

    [Test]
    public void StructLiteralExpression_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse(".{ .x = 1, .y = 2 };");
        AssertNoDiagnostics(diag);

        GlobalStatementSyntax global = (GlobalStatementSyntax)unit.Members[0];
        ExpressionStatementSyntax statement = (ExpressionStatementSyntax)global.Statement;
        StructLiteralExpressionSyntax expression = (StructLiteralExpressionSyntax)statement.Expression;
        Assert.That(expression.Initializers.Count, Is.EqualTo(2));
    }

    [Test]
    public void EmptyStructLiteralExpression_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse(".{ };");
        AssertNoDiagnostics(diag);

        GlobalStatementSyntax global = (GlobalStatementSyntax)unit.Members[0];
        ExpressionStatementSyntax statement = (ExpressionStatementSyntax)global.Statement;
        StructLiteralExpressionSyntax expression = (StructLiteralExpressionSyntax)statement.Expression;
        Assert.That(expression.Initializers.Count, Is.EqualTo(0));
    }

    [Test]
    public void ComptimeExpression_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("comptime { 1; };");
        AssertNoDiagnostics(diag);

        GlobalStatementSyntax global = (GlobalStatementSyntax)unit.Members[0];
        ExpressionStatementSyntax statement = (ExpressionStatementSyntax)global.Statement;
        Assert.That(statement.Expression, Is.TypeOf<ComptimeExpressionSyntax>());
    }

    [Test]
    public void IfExpression_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("reg var choice: u32 = if (cond) left else right;");
        AssertNoDiagnostics(diag);

        VariableDeclarationSyntax decl = (VariableDeclarationSyntax)unit.Members[0];
        Assert.That(decl.Initializer, Is.TypeOf<IfExpressionSyntax>());
    }

    [Test]
    public void MissingTypeName_ReportsDiagnostic()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("reg var value: = 0;");
        Assert.That(unit.Members[0], Is.TypeOf<VariableDeclarationSyntax>());
        Assert.That(diag.Any(d => d.Code == DiagnosticCode.E0104_ExpectedTypeName), Is.True);
    }

    [Test]
    public void NamedType_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("reg var value: CustomType = undefined;");
        AssertNoDiagnostics(diag);

        VariableDeclarationSyntax decl = (VariableDeclarationSyntax)unit.Members[0];
        Assert.That(decl.Type, Is.TypeOf<NamedTypeSyntax>());
    }

    [Test]
    public void ParserSafetyRecovery_SkipsUnconsumedTopLevelToken()
    {
        Token eof = new(TokenKind.EndOfFile, new TextSpan(0, 0), string.Empty);
        Parser parser = CreateParser(new Token(TokenKind.Bad, new TextSpan(0, 1), "~"), eof);

        CompilationUnitSyntax unit = parser.ParseCompilationUnit();

        Assert.That(unit.Members, Has.Count.EqualTo(1));
        Assert.That(parser.Diagnostics.Count, Is.GreaterThan(0));
    }

    [Test]
    public void ParserSafetyRecovery_SkipsUnconsumedBlockToken()
    {
        Parser parser = CreateParser(
            new Token(TokenKind.OpenBrace, new TextSpan(0, 1), "{"),
            new Token(TokenKind.Bad, new TextSpan(1, 1), "~"),
            new Token(TokenKind.CloseBrace, new TextSpan(2, 1), "}"),
            new Token(TokenKind.EndOfFile, new TextSpan(3, 0), string.Empty));

        CompilationUnitSyntax unit = parser.ParseCompilationUnit();

        Assert.That(unit.Members[0], Is.TypeOf<GlobalStatementSyntax>());
        BlockStatementSyntax block = (BlockStatementSyntax)((GlobalStatementSyntax)unit.Members[0]).Statement;
        Assert.That(block.Statements, Has.Count.EqualTo(1));
        Assert.That(parser.Diagnostics.Count, Is.GreaterThan(0));
    }

    [Test]
    public void ParseExpression_AllowsDotOpenBracePostfixRecovery()
    {
        Parser parser = CreateParser(
            new Token(TokenKind.Identifier, new TextSpan(0, 1), "x"),
            new Token(TokenKind.Dot, new TextSpan(1, 1), "."),
            new Token(TokenKind.OpenBrace, new TextSpan(2, 1), "{"),
            new Token(TokenKind.EndOfFile, new TextSpan(3, 0), string.Empty));

        ExpressionSyntax expression = parser.ParseExpression();

        Assert.That(expression, Is.TypeOf<MemberAccessExpressionSyntax>());
        Assert.That(parser.Diagnostics.Count, Is.GreaterThan(0));
    }

    [Test]
    public void UnterminatedAsmBlock_ReachesEndOfFileRecovery()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("asm { {");
        Assert.That(unit.Members[0], Is.TypeOf<GlobalStatementSyntax>());
        Assert.That(diag.Any(d => d.Code == DiagnosticCode.E0101_UnexpectedToken), Is.True);
    }

    [Test]
    public void UnterminatedStructLiteral_ReachesEndOfFileRecovery()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse(".{ .x = 1,");
        Assert.That(unit.Members[0], Is.TypeOf<GlobalStatementSyntax>());
        Assert.That(diag.Any(d => d.Code == DiagnosticCode.E0101_UnexpectedToken), Is.True);
    }

    [Test]
    public void ParseExpression_DotIdentifier_ParsesAsEnumLiteral()
    {
        Parser parser = CreateParser(
            new Token(TokenKind.Dot, new TextSpan(0, 1), "."),
            new Token(TokenKind.Identifier, new TextSpan(1, 1), "x"),
            new Token(TokenKind.EndOfFile, new TextSpan(2, 0), string.Empty));

        ExpressionSyntax expression = parser.ParseExpression();

        Assert.That(expression, Is.TypeOf<EnumLiteralExpressionSyntax>());
        Assert.That(parser.Diagnostics.Count, Is.EqualTo(0));
    }

    [Test]
    public void Parser_PeekBeyondEnd_ReusesLastToken()
    {
        Parser parser = CreateParser(new Token(TokenKind.EndOfFile, new TextSpan(0, 0), string.Empty));

        CompilationUnitSyntax first = parser.ParseCompilationUnit();
        CompilationUnitSyntax second = parser.ParseCompilationUnit();

        Assert.That(first.Members, Is.Empty);
        Assert.That(second.Members, Is.Empty);
    }

    [Test]
    public void IsTypeStart_ReturnsExpectedValueForEveryTokenKind()
    {
        MethodInfo method = typeof(Parser).GetMethod("IsTypeStart", BindingFlags.NonPublic | BindingFlags.Static)!;
        HashSet<TokenKind> typeStarts = new()
        {
            TokenKind.BoolKeyword,
            TokenKind.BitKeyword,
            TokenKind.NitKeyword,
            TokenKind.NibKeyword,
            TokenKind.U8Keyword,
            TokenKind.I8Keyword,
            TokenKind.U16Keyword,
            TokenKind.I16Keyword,
            TokenKind.U32Keyword,
            TokenKind.I32Keyword,
            TokenKind.VoidKeyword,
            TokenKind.UintKeyword,
            TokenKind.IntKeyword,
            TokenKind.U8x4Keyword,
            TokenKind.Star,
            TokenKind.OpenBracket,
            TokenKind.PackedKeyword,
            TokenKind.StructKeyword,
            TokenKind.UnionKeyword,
            TokenKind.EnumKeyword,
            TokenKind.BitfieldKeyword,
            TokenKind.Identifier,
        };

        foreach (TokenKind kind in Enum.GetValues<TokenKind>())
        {
            bool actual = (bool)method.Invoke(null, new object[] { kind })!;
            bool expected = typeStarts.Contains(kind);
            Assert.That(actual, Is.EqualTo(expected), $"Unexpected IsTypeStart result for {kind}");
        }
    }

    [Test]
    public void AsmFunctionDeclaration_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("""
            asm fn add_flags(a: u32, b: u32) -> bool@C {
                ADD {a}, {b} WC
            }
            """);
        AssertNoDiagnostics(diag);

        AsmFunctionDeclarationSyntax function = (AsmFunctionDeclarationSyntax)unit.Members[0];
        Assert.That(function.Name.Text, Is.EqualTo("add_flags"));
        Assert.That(function.VolatileKeyword, Is.Null);
        Assert.That(function.ReturnSpec, Is.Not.Null);
        Assert.That(function.ReturnSpec![0].FlagAnnotation?.Flag.Text, Is.EqualTo("C"));
    }

    [Test]
    public void AsmVolatileFunctionDeclaration_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("""
            asm volatile fn read_value() -> u32 {
                MOV {return}, #10
            }
            """);
        AssertNoDiagnostics(diag);

        AsmFunctionDeclarationSyntax function = (AsmFunctionDeclarationSyntax)unit.Members[0];
        Assert.That(function.VolatileKeyword, Is.Not.Null);
        Assert.That(function.ReturnSpec![0].Type, Is.TypeOf<PrimitiveTypeSyntax>());
    }

    [Test]
    public void AggregateTypes_ParseCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("""
            type Header = union {
                lo: u16,
                hi: u16,
            };
            type Status = enum (u8) {
                Idle,
                Busy = 2,
                ...,
            };
            type Flags = bitfield (u32) {
                carry: bool,
                zero: bool,
            };
            """);
        AssertNoDiagnostics(diag);

        Assert.That(((TypeAliasDeclarationSyntax)unit.Members[0]).Type, Is.TypeOf<UnionTypeSyntax>());
        Assert.That(((TypeAliasDeclarationSyntax)unit.Members[1]).Type, Is.TypeOf<EnumTypeSyntax>());
        Assert.That(((TypeAliasDeclarationSyntax)unit.Members[2]).Type, Is.TypeOf<BitfieldTypeSyntax>());
    }

    [Test]
    public void MultiPointerType_ParsesCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("reg var p: [*]reg u32 = undefined;");
        AssertNoDiagnostics(diag);

        VariableDeclarationSyntax decl = (VariableDeclarationSyntax)unit.Members[0];
        Assert.That(decl.Type, Is.TypeOf<MultiPointerTypeSyntax>());
    }

    [Test]
    public void ArrayAndTypedStructLiterals_ParseCorrectly()
    {
        (CompilationUnitSyntax unit, DiagnosticBag diag) = Parse("""
            {
                [1, 2...];
                Pixel { .r = 1, .g = 2 };
            }
            """);
        AssertNoDiagnostics(diag);

        GlobalStatementSyntax global = (GlobalStatementSyntax)unit.Members[0];
        BlockStatementSyntax block = (BlockStatementSyntax)global.Statement;

        ArrayLiteralExpressionSyntax array = (ArrayLiteralExpressionSyntax)((ExpressionStatementSyntax)block.Statements[0]).Expression;
        Assert.That(array.Elements.Count, Is.EqualTo(2));
        Assert.That(array.Elements[1].Spread, Is.Not.Null);

        ExpressionStatementSyntax typedStructStatement = (ExpressionStatementSyntax)block.Statements[1];
        Assert.That(typedStructStatement.Expression, Is.TypeOf<TypedStructLiteralExpressionSyntax>());
    }
}
