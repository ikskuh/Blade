using Blade.Diagnostics;
using Blade.Source;

namespace Blade.Tests;

[TestFixture]
public class DiagnosticTests
{
    private static readonly TextSpan Span = new(0, 0);
    private static readonly SourceText Source = new(string.Empty);

    [Test]
    public void FormatCode_UsesMessageCode()
    {
        Diagnostic diagnostic = new(new UnexpectedCharacterError(Source, Span, '$'));

        Assert.That(diagnostic.FormatCode(), Is.EqualTo("E0001"));
    }

    [TestCase("E0001", DiagnosticSeverity.Error)]
    [TestCase("W0307", DiagnosticSeverity.Warning)]
    [TestCase("I9002", DiagnosticSeverity.Note)]
    public void GetSeverity_UsesCodePrefix(string code, DiagnosticSeverity expected)
    {
        Assert.That(Diagnostic.GetSeverity(code), Is.EqualTo(expected));
    }
}
