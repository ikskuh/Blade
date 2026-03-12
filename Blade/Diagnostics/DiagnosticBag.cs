using System.Collections;
using System.Collections.Generic;
using Blade.Source;

namespace Blade.Diagnostics;

/// <summary>
/// Collects diagnostics during compilation.
/// </summary>
public sealed class DiagnosticBag : IEnumerable<Diagnostic>
{
    private readonly List<Diagnostic> _diagnostics = new();

    public int Count => _diagnostics.Count;

    public bool HasErrors => _diagnostics.Count > 0;

    public void Report(DiagnosticCode code, TextSpan span, string message)
    {
        _diagnostics.Add(new Diagnostic(code, span, message));
    }

    public void ReportUnexpectedCharacter(TextSpan span, char character)
    {
        Report(DiagnosticCode.E0001_UnexpectedCharacter, span, $"Unexpected character '{character}'.");
    }

    public void ReportUnterminatedString(TextSpan span)
    {
        Report(DiagnosticCode.E0002_UnterminatedString, span, "Unterminated string literal.");
    }

    public void ReportInvalidNumberLiteral(TextSpan span, string text)
    {
        Report(DiagnosticCode.E0003_InvalidNumberLiteral, span, $"Invalid number literal '{text}'.");
    }

    public void ReportUnterminatedBlockComment(TextSpan span)
    {
        Report(DiagnosticCode.E0004_UnterminatedBlockComment, span, "Unterminated block comment.");
    }

    // Parser diagnostics

    public void ReportUnexpectedToken(TextSpan span, string expected, string actual)
    {
        Report(DiagnosticCode.E0101_UnexpectedToken, span, $"Expected {expected}, got '{actual}'.");
    }

    public void ReportExpectedExpression(TextSpan span)
    {
        Report(DiagnosticCode.E0102_ExpectedExpression, span, "Expected expression.");
    }

    public void ReportExpectedStatement(TextSpan span)
    {
        Report(DiagnosticCode.E0103_ExpectedStatement, span, "Expected statement.");
    }

    public void ReportExpectedTypeName(TextSpan span)
    {
        Report(DiagnosticCode.E0104_ExpectedTypeName, span, "Expected type name.");
    }

    public void ReportExpectedIdentifier(TextSpan span)
    {
        Report(DiagnosticCode.E0105_ExpectedIdentifier, span, "Expected identifier.");
    }

    public void ReportInvalidAssignmentTarget(TextSpan span)
    {
        Report(DiagnosticCode.E0106_InvalidAssignmentTarget, span, "Invalid assignment target.");
    }

    public void ReportExpectedSemicolon(TextSpan span)
    {
        Report(DiagnosticCode.E0107_ExpectedSemicolon, span, "Expected ';'.");
    }

    // Semantic diagnostics

    public void ReportSymbolAlreadyDeclared(TextSpan span, string name)
    {
        Report(DiagnosticCode.E0201_SymbolAlreadyDeclared, span, $"Symbol '{name}' is already declared in this scope.");
    }

    public void ReportUndefinedName(TextSpan span, string name)
    {
        Report(DiagnosticCode.E0202_UndefinedName, span, $"Name '{name}' does not exist in the current scope.");
    }

    public void ReportUndefinedType(TextSpan span, string name)
    {
        Report(DiagnosticCode.E0203_UndefinedType, span, $"Type '{name}' is not defined.");
    }

    public void ReportCannotAssignToConstant(TextSpan span, string name)
    {
        Report(DiagnosticCode.E0204_CannotAssignToConstant, span, $"Cannot assign to constant '{name}'.");
    }

    public void ReportTypeMismatch(TextSpan span, string expected, string actual)
    {
        Report(DiagnosticCode.E0205_TypeMismatch, span, $"Type mismatch: expected '{expected}', got '{actual}'.");
    }

    public void ReportNotCallable(TextSpan span, string typeName)
    {
        Report(DiagnosticCode.E0206_NotCallable, span, $"Expression of type '{typeName}' is not callable.");
    }

    public void ReportArgumentCountMismatch(TextSpan span, string functionName, int expected, int actual)
    {
        Report(
            DiagnosticCode.E0207_ArgumentCountMismatch,
            span,
            $"Function '{functionName}' expects {expected} argument(s), but got {actual}.");
    }

    public void ReportInvalidLoopControl(TextSpan span, string keyword)
    {
        Report(DiagnosticCode.E0208_InvalidLoopControl, span, $"'{keyword}' can only be used inside a loop.");
    }

    public void ReportInvalidBreakInRep(TextSpan span)
    {
        Report(DiagnosticCode.E0209_InvalidBreakInRepLoop, span, "'break' is not allowed inside 'rep' loops.");
    }

    public void ReportInvalidYield(TextSpan span)
    {
        Report(DiagnosticCode.E0210_InvalidYieldUsage, span, "'yield' is only allowed inside int1/int2/int3 functions.");
    }

    public void ReportInvalidYieldto(TextSpan span)
    {
        Report(DiagnosticCode.E0211_InvalidYieldtoUsage, span, "'yieldto' is only allowed at top-level or inside coro functions.");
    }

    public void ReportReturnValueCountMismatch(TextSpan span, string functionName, int expected, int actual)
    {
        Report(
            DiagnosticCode.E0212_ReturnValueCountMismatch,
            span,
            $"Function '{functionName}' returns {expected} value(s), but got {actual}.");
    }

    public void ReportReturnOutsideFunction(TextSpan span)
    {
        Report(DiagnosticCode.E0213_ReturnOutsideFunction, span, "'return' is only allowed inside a function.");
    }

    public void ReportInvalidYieldtoTarget(TextSpan span, string target)
    {
        Report(DiagnosticCode.E0214_InvalidYieldtoTarget, span, $"'{target}' is not a coroutine function.");
    }

    public IEnumerator<Diagnostic> GetEnumerator() => _diagnostics.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
