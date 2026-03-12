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

    public IEnumerator<Diagnostic> GetEnumerator() => _diagnostics.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
