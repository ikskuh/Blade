using System;
using System.Linq;
using Blade.Regressions;

namespace Blade.Tests;

[TestFixture]
public sealed class RegressionHarnessTests
{
    [Test]
    public void RegressionReportFormatter_DoesNotRepeatSummaryInDetails()
    {
        RegressionFixtureResult fixtureResult = new(
            "Demonstrators/Language/integer_literals.blade",
            RegressionFixtureOutcome.Fail,
            "unexpected diagnostic: L5, E0101: Expected ';', got '456'.",
            [
                "unexpected diagnostic: L5, E0101: Expected ';', got '456'.",
                "FlexSpin validation was required, but no assembly text was available",
            ],
            artifactDirectoryPath: null);
        RegressionRunResult result = new("/repo", [fixtureResult]);

        string report = RegressionReportFormatter.Format(result);

        Assert.That(
            report.Split(Environment.NewLine).Count(line => line.Contains("unexpected diagnostic: L5, E0101: Expected ';', got '456'.", StringComparison.Ordinal)),
            Is.EqualTo(1));
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
}
