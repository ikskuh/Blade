using Blade.Regressions;

namespace Blade.Tests;

[TestFixture]
public sealed class RegressionHarnessTests
{
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
