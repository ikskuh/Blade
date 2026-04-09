using Blade.Diagnostics;
using Blade.Source;

namespace Blade.Tests;

[TestFixture]
public class DiagnosticTests
{
    private static readonly TextSpan Span = new(0, 0);
    private static readonly SourceText Source = new(string.Empty);

    [TestCase(DiagnosticCode.E0001_UnexpectedCharacter, "E0001")]
    [TestCase(DiagnosticCode.W9001_TestWarning, "W9001")]
    [TestCase(DiagnosticCode.I9002_TestInfo, "I9002")]
    public void FormatCode_UsesSeverityPrefixFromDiagnosticCodeName(DiagnosticCode code, string expected)
    {
        Diagnostic diagnostic = new(Source, code, Span, "message");

        Assert.That(diagnostic.FormatCode(), Is.EqualTo(expected));
    }

    [Test]
    public void FormatCode_FallsBackToErrorPrefixForUnnamedCode()
    {
        Diagnostic diagnostic = new(Source, (DiagnosticCode)1234, Span, "message");

        Assert.That(diagnostic.FormatCode(), Is.EqualTo("E1234"));
    }
}
