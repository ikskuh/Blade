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
}
