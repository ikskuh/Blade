using System;
using System.Linq;
using Blade.Regressions;

namespace Blade.Tests;

[TestFixture]
public sealed class RegressionHarnessTests
{
    [Test]
    public void RegressionReportFormatter_UsesCompactLayoutAndBottomFailureDetails()
    {
        RegressionFixtureResult passResult = new(
            "Demonstrators/Asm/asm_label.blade",
            RegressionFixtureOutcome.Pass,
            "passed",
            [],
            artifactDirectoryPath: null);
        RegressionFixtureResult failResult = new(
            "Demonstrators/Language/integer_literals.blade",
            RegressionFixtureOutcome.Fail,
            "unexpected diagnostic: L5, E0101: Expected ';', got '456'.",
            [
                "unexpected diagnostic: L5, E0101: Expected ';', got '456'.",
                "FlexSpin validation was required, but no assembly text was available",
            ],
            artifactDirectoryPath: "/repo/.artifacts/regressions/run/fail");
        RegressionFixtureResult xfailResult = new(
            "RegressionTests/ExpectedFailures/hub_string_walk.blade",
            RegressionFixtureOutcome.XFail,
            "expected failure observed",
            [
                "missing diagnostic code E0202: expected at least 1, got 0",
            ],
            artifactDirectoryPath: null);
        RegressionRunResult result = new("/repo", [passResult, failResult, xfailResult]);

        string report = RegressionReportFormatter.Format(result);
        string[] lines = report.Split(Environment.NewLine, StringSplitOptions.None);

        Assert.That(lines[0], Is.EqualTo("PASS           Demonstrators/Asm/asm_label.blade"));
        Assert.That(lines[1], Is.EqualTo("FAIL           Demonstrators/Language/integer_literals.blade"));
        Assert.That(lines[2], Is.EqualTo("XFAIL          RegressionTests/ExpectedFailures/hub_string_walk.blade"));
        Assert.That(report, Does.Not.Contain("PASS           Demonstrators/Asm/asm_label.blade" + Environment.NewLine + "  passed"));
        Assert.That(report, Does.Not.Contain("XFAIL          RegressionTests/ExpectedFailures/hub_string_walk.blade" + Environment.NewLine + "  expected failure observed"));
        Assert.That(report, Does.Contain(Environment.NewLine + "---" + Environment.NewLine + Environment.NewLine + "FAIL           Demonstrators/Language/integer_literals.blade"));
        Assert.That(report, Does.Contain("  unexpected diagnostic: L5, E0101: Expected ';', got '456'."));
        Assert.That(report, Does.Contain("  FlexSpin validation was required, but no assembly text was available"));
        Assert.That(report, Does.Contain("  artifacts: .artifacts/regressions/run/fail"));
        Assert.That(
            report.Split(Environment.NewLine).Count(line => line.Contains("unexpected diagnostic: L5, E0101: Expected ';', got '456'.", StringComparison.Ordinal)),
            Is.EqualTo(1));
        Assert.That(report.TrimEnd(), Does.EndWith("1 failed, 1 xfailed, 1 passed, 3 total"));
    }

    [Test]
    public void RegressionReportFormatter_SkipsZeroCountSummaryEntriesAndDoesNotExpandSkips()
    {
        RegressionFixtureResult skipResult = new(
            "RegressionTests/Assembly/raw_exact.pasm2",
            RegressionFixtureOutcome.Skipped,
            "skipped",
            [
                "skipped: flexspin is not available",
            ],
            artifactDirectoryPath: null);
        RegressionRunResult result = new("/repo", [skipResult]);

        string report = RegressionReportFormatter.Format(result);

        Assert.That(report, Does.Not.Contain("---"));
        Assert.That(report, Does.Not.Contain("skipped: flexspin is not available"));
        Assert.That(report.TrimEnd(), Does.EndWith("1 total"));
    }

    [Test]
    public void FullRegressionSuite_Passes()
    {
        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            WriteFailureArtifacts = true,
        });

        if (!result.Succeeded)
            Assert.Fail(RegressionReportFormatter.Format(result));

        Assert.That(result.FixtureResults, Is.Not.Empty);
    }

    [Test]
    public void BladeCrashFixture_PassesWhenCompilationProducesDiagnosticsButDoesNotThrow()
    {
        using TempDirectory temp = new();
        temp.MakeDir("Examples");
        temp.MakeDir("Demonstrators");
        temp.MakeDir("Blade.Tests");
        temp.WriteFile("justfile", "fuzz:\n    false\n");
        temp.WriteFile("RegressionTests/syntax_failure.blade.crash", "fn main(");

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(fixtureResult.RelativePath, Is.EqualTo("RegressionTests/syntax_failure.blade.crash"));
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Pass));
            Assert.That(fixtureResult.Summary, Is.EqualTo("passed"));
            Assert.That(fixtureResult.Details, Is.Empty);
        });
    }

    [Test]
    public void BladeCrashFixture_PassesWhenSourceIsInvalidUtf8ButCompilerDoesNotThrow()
    {
        using TempDirectory temp = new();
        temp.MakeDir("Examples");
        temp.MakeDir("Demonstrators");
        temp.MakeDir("Blade.Tests");
        temp.WriteFile("justfile", "fuzz:\n    false\n");
        temp.WriteFile("RegressionTests/invalid_utf8.blade.crash", new byte[] { 0x80, 0x61 });

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(fixtureResult.RelativePath, Is.EqualTo("RegressionTests/invalid_utf8.blade.crash"));
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Pass));
            Assert.That(fixtureResult.Summary, Is.EqualTo("passed"));
        });
    }
}
