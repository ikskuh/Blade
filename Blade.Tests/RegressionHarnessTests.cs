using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Blade.HwTestRunner;
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
            RegressionFixtureOutcome.Ok,
            "ok",
            [],
            null,
            false);
        RegressionFixtureResult failResult = new(
            "Demonstrators/Language/integer_literals.blade",
            RegressionFixtureOutcome.Fail,
            "unexpected diagnostic: L5, UnexpectedToken: Expected ';', got '456'.",
            [
                "unexpected diagnostic: L5, UnexpectedToken: Expected ';', got '456'.",
                "FlexSpin validation was required, but no assembly text was available",
            ],
            "/repo/.artifacts/regressions/run/fail",
            false);
        RegressionFixtureResult xfailResult = new(
            "RegressionTests/ExpectedFailures/hub_string_walk.blade",
            RegressionFixtureOutcome.XFail,
            "failed as expected",
            [
                "missing diagnostic UndefinedName: expected at least 1, got 0",
            ],
            null,
            false);
        RegressionRunResult result = new("/repo", [passResult, failResult, xfailResult]);

        string report = RegressionReportFormatter.Format(result);
        string[] lines = report.Split(Environment.NewLine, StringSplitOptions.None);

        Assert.That(lines[0], Is.EqualTo("OK             Demonstrators/Asm/asm_label.blade"));
        Assert.That(lines[1], Is.EqualTo("FAIL           Demonstrators/Language/integer_literals.blade"));
        Assert.That(lines[2], Is.EqualTo("XFAIL          RegressionTests/ExpectedFailures/hub_string_walk.blade"));
        Assert.That(report, Does.Not.Contain("OK             Demonstrators/Asm/asm_label.blade" + Environment.NewLine + "  ok"));
        Assert.That(report, Does.Not.Contain("XFAIL          RegressionTests/ExpectedFailures/hub_string_walk.blade" + Environment.NewLine + "  failed as expected"));
        Assert.That(report, Does.Contain(Environment.NewLine + "---" + Environment.NewLine + Environment.NewLine + "FAIL           Demonstrators/Language/integer_literals.blade"));
        Assert.That(report, Does.Contain("  unexpected diagnostic: L5, UnexpectedToken: Expected ';', got '456'."));
        Assert.That(report, Does.Contain("  FlexSpin validation was required, but no assembly text was available"));
        Assert.That(report, Does.Contain("  artifacts: .artifacts/regressions/run/fail"));
        Assert.That(
            report.Split(Environment.NewLine).Count(line => line.Contains("unexpected diagnostic: L5, UnexpectedToken: Expected ';', got '456'.", StringComparison.Ordinal)),
            Is.EqualTo(1));
        Assert.That(report.TrimEnd(), Does.EndWith("1 fail, 1 xfail, 1 ok, 3 total"));
    }

    [Test]
    public void RegressionReportFormatter_SkipsZeroCountSummaryEntriesAndDoesNotExpandSkips()
    {
        RegressionFixtureResult skipResult = new(
            "RegressionTests/raw_exact.blade",
            RegressionFixtureOutcome.Skipped,
            "skipped",
            [
                "skipped: flexspin is not available",
            ],
            null,
            false);
        RegressionRunResult result = new("/repo", [skipResult]);

        string report = RegressionReportFormatter.Format(result);

        Assert.That(report, Does.Not.Contain("---"));
        Assert.That(report, Does.Not.Contain("skipped: flexspin is not available"));
        Assert.That(report.TrimEnd(), Does.EndWith("1 total"));
    }

    [Test]
    public void RegressionReportFormatter_EmitsExceptionStackTraceLinesForCrashFixtures()
    {
        RegressionFixtureResult failResult = new(
            "RegressionTests/Fuzzing/issue-00001.blade.crash",
            RegressionFixtureOutcome.Fail,
            "fixture evaluation crashed",
            [
                "Unhandled regression runner error: boom",
                "Exception stack trace:",
                "System.InvalidOperationException: boom",
                "   at Blade.CompilerDriver.CompileFile(String filePath)",
                "   at Blade.Regressions.RegressionRunner.ExecuteBladeCrashFixture(RegressionFixture fixture)",
            ],
            null,
            false);
        RegressionRunResult result = new("/repo", [failResult]);

        string report = RegressionReportFormatter.Format(result);

        Assert.That(report, Does.Contain("  Exception stack trace:"));
        Assert.That(report, Does.Contain("  System.InvalidOperationException: boom"));
        Assert.That(report, Does.Contain("     at Blade.CompilerDriver.CompileFile(String filePath)"));
        Assert.That(report, Does.Contain("     at Blade.Regressions.RegressionRunner.ExecuteBladeCrashFixture(RegressionFixture fixture)"));
    }

    [Test]
    public void RegressionReportFormatter_HwErr_IsLabelledAndExpandedAndMarksRunUnsuccessful()
    {
        RegressionFixtureResult passResult = new(
            "Demonstrators/simple.blade",
            RegressionFixtureOutcome.Ok,
            "ok",
            [],
            null,
            false);
        RegressionFixtureResult hwFailedResult = new(
            "Demonstrators/HwTest/hw_exec.blade",
            RegressionFixtureOutcome.HwErr,
            "hardware run 1 [] failed: port not found",
            ["hardware run 1 [] failed: port not found"],
            null,
            false);
        RegressionRunResult result = new("/repo", [passResult, hwFailedResult]);

        string report = RegressionReportFormatter.Format(result);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.HwErrCount, Is.EqualTo(1));
            Assert.That(report, Does.Contain("HW ERR"));
            Assert.That(report, Does.Contain("hardware run 1 [] failed: port not found"));
            Assert.That(report.TrimEnd(), Does.EndWith("1 hw err, 1 ok, 2 total"));
        });
    }

    [Test]
    public void ConfigDrivenRegressionSuite_Passes()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = true,
        });

        if (!result.Succeeded)
            Assert.Fail(RegressionReportFormatter.Format(result));

        Assert.That(result.FixtureResults, Is.Not.Empty);
    }

    [Test]
    public void ConfigDrivenRegressionSuite_PrunesArtifactRunsToLastTenEntries()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("RegressionTests/retention_failure.blade", """
        cog task main {
            missing_symbol();
        }
        """);

        string regressionsArtifactRoot = Path.Combine(temp.Path, ".artifacts", "regressions");
        Directory.CreateDirectory(regressionsArtifactRoot);
        for (int index = 0; index < 11; index++)
            Directory.CreateDirectory(Path.Combine(regressionsArtifactRoot, $"20240101T0000000{index}Z"));

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = true,
        });

        RegressionFixtureResult failedFixture = result.FixtureResults.Single(fixture =>
            fixture.RelativePath == "RegressionTests/retention_failure.blade");

        Assert.That(result.Succeeded, Is.False);
        Assert.That(failedFixture.ArtifactDirectoryPath, Is.Not.Null);

        string[] remainingEntries = Directory
            .GetFileSystemEntries(regressionsArtifactRoot)
            .Select(Path.GetFileName)
            .Where(static name => name is not null)
            .Cast<string>()
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.That(remainingEntries, Has.Length.EqualTo(10));
        Assert.That(remainingEntries, Does.Not.Contain("20240101T00000000Z"));
        Assert.That(
            remainingEntries,
            Does.Contain(Path.GetFileName(Path.GetDirectoryName(failedFixture.ArtifactDirectoryPath!))));
    }

    [Test]
    public void PassHwFixture_WithoutConfiguredPort_UsesConfiguredHardwareRuntimeAndPasses()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        WriteHardwareRuntime(temp);
        temp.WriteFile("Demonstrators/hw_runtime_injected.blade", """
        // EXPECT: pass-hw
        // RUNS:
        // - [ 0x10, -1 ] = 0xF
        // STAGE: final-asm
        // CONTAINS:
        // - g_rt_result LONG 0
        cog task main {
            var x: u32 = 0;
            _ = x;
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            HardwarePort = "",  // disable hardware; test only verifies runtime injection via CONTAINS
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(result =>
            result.RelativePath == "Demonstrators/hw_runtime_injected.blade");
        Assert.Multiple(() =>
        {
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Ok));
            Assert.That(fixtureResult.Summary, Is.EqualTo("ok"));
        });
    }

    [Test]
    public void PassHwFixture_WithExplicitRuntime_KeepsExplicitRuntimeInsteadOfConfiguredRuntime()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        WriteHardwareRuntime(temp);
        temp.WriteFile("Demonstrators/custom_runtime.blade", """
        import builtin;

        layout Runtime {
            cog var custom_marker: u32 = 0;
        }

        cog task _start : Runtime {
            builtin.init_memory();
            builtin.task_main();
        }
        """);
        temp.WriteFile("Demonstrators/hw_explicit_runtime.blade", """
        // EXPECT: pass-hw
        // RUNS:
        // - [] = 0x0
        // ARGS: --runtime=custom_runtime.blade
        // STAGE: final-asm
        // CONTAINS:
        // - g_custom_marker LONG 0
        // ! g_rt_result LONG 0
        cog task main {
            var x: u32 = 1;
            _ = x;
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            HardwarePort = "",  // disable hardware; test only verifies CONTAINS/ARGS behavior
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(result =>
            result.RelativePath == "Demonstrators/hw_explicit_runtime.blade");
        Assert.Multiple(() =>
        {
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Ok));
        });
    }

    [Test]
    public void PassHwFixture_RequiresRunsDirective()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/missing_runs.blade", """
        // EXPECT: pass-hw
        var x: u32 = 0;
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(result =>
            result.RelativePath == "Demonstrators/missing_runs.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Fail));
            Assert.That(fixtureResult.Details, Has.Some.Contains("EXPECT: pass-hw requires RUNS."));
        });
    }

    [Test]
    public void RunsDirective_IsRejectedForPlainPassFixture()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/runs_on_plain_pass.blade", """
        // EXPECT: pass
        // RUNS:
        // - [] = 0x0
        var x: u32 = 0;
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(result =>
            result.RelativePath == "Demonstrators/runs_on_plain_pass.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Fail));
            Assert.That(fixtureResult.Details, Has.Some.Contains("RUNS is only valid with EXPECT: pass-hw or EXPECT: xfail-hw."));
        });
    }

    [Test]
    public void OutputDirective_IsRejectedAfterHeaderStarts()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/output_header.blade", """
        // EXPECT: pass-hw
        // OUTPUT: 0x0
        var x: u32 = 0;
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(result =>
            result.RelativePath == "Demonstrators/output_header.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Fail));
            Assert.That(fixtureResult.Details, Has.Some.Contains("Unsupported header directive 'OUTPUT'."));
        });
    }

    [Test]
    public void RegressionCommandLine_UsesEnvironmentHardwarePortWhenCliFlagIsAbsent()
    {
        string? previous = Environment.GetEnvironmentVariable("BLADE_TEST_PORT");
        try
        {
            Environment.SetEnvironmentVariable("BLADE_TEST_PORT", "env-port");
            RegressionRunOptions options = ParseRegressionCommandLine();
            Assert.That(options.HardwarePort, Is.EqualTo("env-port"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLADE_TEST_PORT", previous);
        }
    }

    [Test]
    public void RegressionCommandLine_CliHardwarePortOverridesEnvironment()
    {
        string? previous = Environment.GetEnvironmentVariable("BLADE_TEST_PORT");
        try
        {
            Environment.SetEnvironmentVariable("BLADE_TEST_PORT", "env-port");
            RegressionRunOptions options = ParseRegressionCommandLine("--hw-port", "cli-port");
            Assert.That(options.HardwarePort, Is.EqualTo("cli-port"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLADE_TEST_PORT", previous);
        }
    }

    [Test]
    [NonParallelizable]
    public void RegressionCommandLine_ParsesHardwareLoaderFlag()
    {
        RegressionRunOptions turbopropOptions = ParseRegressionCommandLine("--hw-loader", "turboprop");
        RegressionRunOptions loadp2Options = ParseRegressionCommandLine("--hw-loader", "loadp2");

        Assert.Multiple(() =>
        {
            Assert.That(turbopropOptions.HardwareLoader, Is.EqualTo(HardwareLoaderKind.Turboprop));
            Assert.That(loadp2Options.HardwareLoader, Is.EqualTo(HardwareLoaderKind.Loadp2));
        });
    }

    [Test]
    [NonParallelizable]
    public void RegressionCommandLine_UsesEnvironmentHardwareLoaderWhenCliFlagIsAbsent()
    {
        string? previous = Environment.GetEnvironmentVariable(HardwareLoaderSettings.LoaderEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(HardwareLoaderSettings.LoaderEnvironmentVariable, "turboprop");
            RegressionRunOptions options = ParseRegressionCommandLine();
            Assert.That(options.HardwareLoader, Is.EqualTo(HardwareLoaderKind.Turboprop));
        }
        finally
        {
            Environment.SetEnvironmentVariable(HardwareLoaderSettings.LoaderEnvironmentVariable, previous);
        }
    }

    [Test]
    [NonParallelizable]
    public void RegressionCommandLine_CliHardwareLoaderOverridesEnvironment()
    {
        string? previous = Environment.GetEnvironmentVariable(HardwareLoaderSettings.LoaderEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(HardwareLoaderSettings.LoaderEnvironmentVariable, "turboprop");
            RegressionRunOptions options = ParseRegressionCommandLine("--hw-loader", "loadp2");
            Assert.That(options.HardwareLoader, Is.EqualTo(HardwareLoaderKind.Loadp2));
        }
        finally
        {
            Environment.SetEnvironmentVariable(HardwareLoaderSettings.LoaderEnvironmentVariable, previous);
        }
    }

    [Test]
    [NonParallelizable]
    public void RegressionCommandLine_UsesEnvironmentTurbopropNoVersionCheckWhenCliFlagIsAbsent()
    {
        string? previous = Environment.GetEnvironmentVariable(HardwareLoaderSettings.TurbopropNoVersionCheckEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(HardwareLoaderSettings.TurbopropNoVersionCheckEnvironmentVariable, "true");
            RegressionRunOptions options = ParseRegressionCommandLine();
            Assert.That(options.HardwareTurbopropNoVersionCheck, Is.True);
        }
        finally
        {
            Environment.SetEnvironmentVariable(HardwareLoaderSettings.TurbopropNoVersionCheckEnvironmentVariable, previous);
        }
    }

    [Test]
    [NonParallelizable]
    public void RegressionCommandLine_ParsesTurbopropNoVersionCheckFlag()
    {
        RegressionRunOptions options = ParseRegressionCommandLine("--hw-turboprop-no-version-check");
        Assert.That(options.HardwareTurbopropNoVersionCheck, Is.True);
    }

    [Test]
    [NonParallelizable]
    public void RegressionCommandLine_VersionCheckFlagOverridesEnvironment()
    {
        string? previous = Environment.GetEnvironmentVariable(HardwareLoaderSettings.TurbopropNoVersionCheckEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(HardwareLoaderSettings.TurbopropNoVersionCheckEnvironmentVariable, "true");
            RegressionRunOptions options = ParseRegressionCommandLine("--hw-turboprop-version-check");
            Assert.That(options.HardwareTurbopropNoVersionCheck, Is.False);
        }
        finally
        {
            Environment.SetEnvironmentVariable(HardwareLoaderSettings.TurbopropNoVersionCheckEnvironmentVariable, previous);
        }
    }

    [Test]
    public void RegressionCommandLine_ParsesJsonFlag()
    {
        RegressionRunOptions options = ParseRegressionCommandLine("--json");
        Assert.That(options.Json, Is.True);
    }

    [Test]
    public void RegressionCommandLine_ParsesConfigFlag()
    {
        RegressionRunOptions options = ParseRegressionCommandLine("--config", "custom-regressions.json");
        Assert.That(options.ConfigPath, Is.EqualTo("custom-regressions.json"));
    }

    [Test]
    public void RegressionRunner_UsesConfigPathOverride()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            ConfigPath = Path.Combine(temp.Path, "regressions.cfg.json"),
            WriteFailureArtifacts = false,
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.RepositoryRootPath, Is.EqualTo(temp.Path));
            Assert.That(result.FixtureResults.Select(static item => item.RelativePath), Is.EqualTo(["Examples/smoke.blade"]));
        });
    }

    [Test]
    public void RegressionRunner_ConfigAcceptsCommentsAndTrailingCommas()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        WriteRegressionConfig(temp, """
        {
            // Parser options must allow comments and trailing commas.
            "pools": [
                {
                    "path": "Examples",
                    "expect": "accept",
                },
            ],
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.FixtureResults.Select(static item => item.RelativePath), Is.EqualTo(["Examples/smoke.blade"]));
        });
    }

    [Test]
    public void AcceptPool_IgnoresInFileExpectDirectivesAndRequiresCleanCompile()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Examples/header_is_ignored.blade", """
        // EXPECT: fail
        fn broken(
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(static result =>
            result.RelativePath == "Examples/header_is_ignored.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Fail));
            Assert.That(fixtureResult.Details, Has.Some.StartsWith("unexpected diagnostic:"));
        });
    }

    [Test]
    public void RejectPool_IgnoresLegacyFirstLineDiagnosticComments()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Blade.Tests/Reject/legacy_reject.blade", """
        // E9999
        fn demo() void {
            missing();
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(static result =>
            result.RelativePath == "Blade.Tests/Reject/legacy_reject.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Ok));
        });
    }

    [Test]
    public void BladeCrashFixture_IsRejectedOutsideEncodedPools()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Examples/not_encoded.blade.crash", new byte[] { 0x80 });

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(static result =>
            result.RelativePath == "Examples/not_encoded.blade.crash");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Fail));
            Assert.That(fixtureResult.Details, Has.Some.Contains(".blade.crash fixtures are only valid in encoded pools."));
        });
    }

    [Test]
    public void PassHwFixture_WithConfiguredPort_HwRunFails_IsHwErr()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        WriteHardwareRuntime(temp);
        temp.WriteFile("Demonstrators/hw_exec.blade", """
        // EXPECT: pass-hw
        // RUNS:
        // - [] = 0x0
        cog task main {
            var x: u32 = 0;
            _ = x;
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            HardwarePort = "/definitely/not/a/serial/port",
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(result =>
            result.RelativePath == "Demonstrators/hw_exec.blade");
        if (fixtureResult.Outcome == RegressionFixtureOutcome.Skipped)
            Assert.Ignore("flexspin is not available in this environment");

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.HwErr));
            Assert.That(fixtureResult.Details, Has.Some.StartsWith("hardware run 1 [] failed:"));
        });
    }

    [Test]
    public void XFailHwFixture_WithConfiguredPort_HwRunFails_IsHwErr()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        WriteHardwareRuntime(temp);
        temp.WriteFile("Demonstrators/hw_xfail_exec.blade", """
        // EXPECT: xfail-hw
        // RUNS:
        // - [] = 0x1
        cog task main {
            var x: u32 = 0;
            _ = x;
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            HardwarePort = "/definitely/not/a/serial/port",
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(result =>
            result.RelativePath == "Demonstrators/hw_xfail_exec.blade");
        if (fixtureResult.Outcome == RegressionFixtureOutcome.Skipped)
            Assert.Ignore("flexspin is not available in this environment");

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.HwErr));
            Assert.That(fixtureResult.Details, Has.Some.StartsWith("hardware run 1 [] failed:"));
        });
    }

    [Test]
    public void HwErrFixture_WritesArtifactsWhenEnabled()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        WriteHardwareRuntime(temp);
        temp.WriteFile("Demonstrators/hw_artifacts.blade", """
        // EXPECT: pass-hw
        // RUNS:
        // - [] = 0x0
        cog task main {
            var x: u32 = 0;
            _ = x;
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = true,
            HardwarePort = "/definitely/not/a/serial/port",
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(item =>
            item.RelativePath == "Demonstrators/hw_artifacts.blade");
        if (fixtureResult.Outcome == RegressionFixtureOutcome.Skipped)
            Assert.Ignore("flexspin is not available in this environment");

        Assert.Multiple(() =>
        {
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.HwErr));
            Assert.That(fixtureResult.HardwareAttempted, Is.True);
            Assert.That(fixtureResult.ArtifactDirectoryPath, Is.Not.Null);
            Assert.That(File.Exists(Path.Combine(fixtureResult.ArtifactDirectoryPath!, "issues.txt")), Is.True);
        });
    }

    [Test]
    public void HwFailFixture_WritesEscapedPerRunHardwareDumps()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        WriteHardwareRuntime(temp);
        temp.WriteFile("Demonstrators/hw_dump_artifacts.blade", """
        // EXPECT: pass-hw
        // RUNS:
        // - [0x1] = 0x10
        // - [0x2] = 0x20
        cog task main {
            var x: u32 = 0;
            _ = x;
        }
        """);

        temp.MakeDir("tools");
        WriteExecutable(temp.GetFullPath("tools/turboprop"), """
        #!/bin/sh
        count_file="$BLADE_TEST_TURBOPROP_COUNTER"
        count=1
        if [ -f "$count_file" ]; then
            count=$(( $(/bin/cat "$count_file") + 1 ))
        fi
        printf '%s' "$count" > "$count_file"
        /bin/cat >/dev/null
        if [ "$count" -eq 1 ]; then
            printf '\002alpha\r\nbeta\t\00300000010\n\004'
            printf 'stderr-one\r\n' >&2
        else
            printf '\002second\n\00300000021\n\004'
            printf 'stderr-two\004' >&2
        fi
        """);

        using EnvironmentScope environment = new();
        string? currentPath = Environment.GetEnvironmentVariable("PATH");
        string toolSearchPath = string.IsNullOrWhiteSpace(currentPath)
            ? temp.GetFullPath("tools")
            : temp.GetFullPath("tools") + Path.PathSeparator + currentPath;
        environment.Set("PATH", toolSearchPath);
        environment.Set("BLADE_TEST_TURBOPROP_COUNTER", temp.GetFullPath("turboprop-count.txt"));

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = true,
            HardwarePort = "/dev/fake-p2",
            HardwareLoader = HardwareLoaderKind.Turboprop,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(item =>
            item.RelativePath == "Demonstrators/hw_dump_artifacts.blade");
        if (fixtureResult.Outcome == RegressionFixtureOutcome.Skipped)
            Assert.Ignore("flexspin is not available in this environment");

        Assert.That(fixtureResult.ArtifactDirectoryPath, Is.Not.Null);
        string artifactDirectoryPath = fixtureResult.ArtifactDirectoryPath!;
        string firstRunDumpPath = Path.Combine(artifactDirectoryPath, "hardware-run-01.txt");
        string secondRunDumpPath = Path.Combine(artifactDirectoryPath, "hardware-run-02.txt");
        string firstRunDump = File.ReadAllText(firstRunDumpPath);
        string secondRunDump = File.ReadAllText(secondRunDumpPath);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.HwFail));
            Assert.That(File.Exists(Path.Combine(artifactDirectoryPath, "issues.txt")), Is.True);
            Assert.That(File.Exists(firstRunDumpPath), Is.True);
            Assert.That(File.Exists(secondRunDumpPath), Is.True);
            Assert.That(firstRunDump, Does.Contain("Run: 1"));
            Assert.That(firstRunDump, Does.Contain("Arguments: [0x1]"));
            Assert.That(firstRunDump, Does.Contain("Expected Outputs:"));
            Assert.That(firstRunDump, Does.Contain("[0] 0x00000010 | unsigned 16 | signed 16"));
            Assert.That(firstRunDump, Does.Contain("[0] 0x00000001 | unsigned 1 | signed 1"));
            Assert.That(firstRunDump, Does.Contain("<CR><LF>" + Environment.NewLine + "beta<TAB>"));
            Assert.That(firstRunDump, Does.Contain("00000010<LF>" + Environment.NewLine + "<EOT>"));
            Assert.That(firstRunDump, Does.Contain("stderr-one<CR><LF>" + Environment.NewLine));
            Assert.That(secondRunDump, Does.Contain("Run: 2"));
            Assert.That(secondRunDump, Does.Contain("Arguments: [0x2]"));
            Assert.That(secondRunDump, Does.Contain("Expected Outputs:"));
            Assert.That(secondRunDump, Does.Contain("[0] 0x00000020 | unsigned 32 | signed 32"));
            Assert.That(secondRunDump, Does.Contain("[0] 0x00000021 | unsigned 33 | signed 33"));
            Assert.That(secondRunDump, Does.Contain("second<LF>" + Environment.NewLine + "<ETX>00000021<LF>" + Environment.NewLine + "<EOT>"));
            Assert.That(secondRunDump, Does.Contain("stderr-two<EOT>"));
        });
    }

    [Test]
    public void MatcherTraceFormatter_RendersBindingsMatchLocationsAndFailures()
    {
        object traceReport = EvaluateMatcherTraceReport(
            """
            MOV PA, #0
            ADD PA, #1
            MOV PB, #0
            ADD PB, #1
            NOP
            RET
            """);

        string traceText = RenderMatcherTraceReport(traceReport);

        Assert.Multiple(() =>
        {
            Assert.That(traceText, Does.Contain("Stage: final-asm"));
            Assert.That(traceText, Does.Contain("CONTAINS block 1: FAIL"));
            Assert.That(traceText, Does.Contain("SEQUENCE block 1: PASS"));
            Assert.That(traceText, Does.Contain("SEQUENCE block 2: FAIL"));
            Assert.That(traceText, Does.Contain("SEQUENCE block 3: FAIL"));
            Assert.That(traceText, Does.Contain("EXACT block 1: FAIL"));
            Assert.That(traceText, Does.Contain("?1 = PA"));
            Assert.That(traceText, Does.Contain("Search start line index: 0"));
            Assert.That(traceText, Does.Contain("source line 1: MOV PA , # 0"));
            Assert.That(traceText, Does.Contain("line index 0"));
            Assert.That(traceText, Does.Contain("matched: MOV PA , # 0"));
            Assert.That(traceText, Does.Contain("expected 3 occurrence(s) of snippet, found 1: Pattern { Source = ADD ?1, #1"));
            Assert.That(traceText, Does.Contain("unexpected snippet in sequence gap: Pattern { Source = MOV PB, #0"));
            Assert.That(traceText, Does.Contain("missing ordered snippet: Pattern { Source = JMP #done"));
            Assert.That(traceText, Does.Contain("unexpected text between exact snippets before: RET"));
            Assert.That(traceText, Does.Contain("Bindings: none"));
        });
    }

    [Test]
    public void SequenceLabelPattern_MatchesStandaloneLabelLine_NotCallOperandSubstring()
    {
        RegressionExpectation expectation = new(
            RegressionExpectationKind.Pass,
            RegressionStage.FinalAsm,
            [],
            [
                new SnippetBlock(
                [
                    CreatePositiveSnippetItem("f_two_ret"),
                    CreatePositiveSnippetItem("MOV ?1, PA"),
                ]),
            ],
            [],
            [],
            [],
            FlexspinExpectation.Forbidden,
            [],
            []);

        CodeAssertionTestResult result = EvaluateMatcherTrace(expectation,
            """
            CALLPA main_r487, #f_two_ret
            f_two_ret
            MOV main_r487, PA
            """);

        Assert.Multiple(() =>
        {
            Assert.That(result.Issues, Is.Empty);
            Assert.That(result.TraceText, Does.Contain("SEQUENCE block 1: PASS"));
            Assert.That(result.TraceText, Does.Contain("source line 2: f_two_ret"));
            Assert.That(result.TraceText, Does.Not.Contain("source line 1: CALLPA main_r487 , # f_two_ret" + Environment.NewLine + "      matched: f_two_ret"));
        });
    }

    [Test]
    public void ContainsFragmentPattern_DoesNotMatchInsideLongerInstructionLine()
    {
        RegressionExpectation expectation = new(
            RegressionExpectationKind.Pass,
            RegressionStage.FinalAsm,
            [
                new SnippetBlock(
                [
                    CreatePositiveSnippetItem("ADD"),
                ]),
            ],
            [],
            [],
            [],
            [],
            FlexspinExpectation.Forbidden,
            [],
            []);

        CodeAssertionTestResult result = EvaluateMatcherTrace(expectation,
            """
            ADD PA, #1
            """);

        Assert.Multiple(() =>
        {
            Assert.That(result.Issues, Has.Count.EqualTo(1));
            Assert.That(result.Issues[0], Does.StartWith("missing snippet: Pattern { Source = ADD"));
            Assert.That(result.TraceText, Does.Contain("CONTAINS block 1: FAIL"));
            Assert.That(result.TraceText, Does.Contain("Matches: none"));
        });
    }

    [Test]
    public void CodeNormalizer_NormalizeText_SplitsSegmentsAndRejectsLineFeeds()
    {
        NormalizedText normalized = NormalizeMatcherLine("MOV PA, #0");
        IReadOnlyList<string> segments = GetNormalizedSegments(normalized);

        Assert.Multiple(() =>
        {
            Assert.That(segments, Is.EqualTo(new[] { "MOV", "PA", ",", "#", "0" }));
            Assert.That(AssertThrowsFromNormalizeMatcherLine("MOV\nPA"), Is.TypeOf<ArgumentException>());
        });
    }

    [Test]
    public void CodeNormalizer_NormalizeBladeStageAndAssemblyText_HandleSingleLineSegments()
    {
        string semicolonText =
            """
            MOV PA, #0 ; comment

            ADD PA, #1
            """;
        string assemblyText =
            """
            MOV PA, #0 ' comment

            ADD PA, #1
            """;

        foreach (RegressionStage stage in new[]
                 {
                     RegressionStage.Bound,
                     RegressionStage.MirPreOptimization,
                     RegressionStage.Mir,
                     RegressionStage.LirPreOptimization,
                     RegressionStage.Lir,
                 })
        {
            NormalizedSourceText normalized = NormalizeBladeStage(stage, semicolonText);
            Assert.That(GetNormalizedLineTexts(normalized), Is.EqualTo(new[] { "MOV PA , # 0", "ADD PA , # 1" }));
            Assert.That(GetNormalizedSourceLineNumbers(normalized), Is.EqualTo(new[] { 1, 3 }));
        }

        foreach (RegressionStage stage in new[]
                 {
                     RegressionStage.AsmirPreOptimization,
                     RegressionStage.Asmir,
                     RegressionStage.FinalAsm,
                 })
        {
            NormalizedSourceText normalized = NormalizeBladeStage(stage, assemblyText);
            Assert.That(GetNormalizedLineTexts(normalized), Is.EqualTo(new[] { "MOV PA , # 0", "ADD PA , # 1" }));
            Assert.That(GetNormalizedSourceLineNumbers(normalized), Is.EqualTo(new[] { 1, 3 }));
        }

        NormalizedSourceText normalizedAssembly = NormalizeAssemblyText(assemblyText);

        Assert.Multiple(() =>
        {
            Assert.That(GetNormalizedGapText(normalizedAssembly, 0, 2), Is.EqualTo("MOV PA , # 0" + Environment.NewLine + "ADD PA , # 1"));
            Assert.That(GetNormalizedGapText(normalizedAssembly, 1, 1), Is.Empty);
            Assert.That(AssertThrowsFromNormalizeBladeStage((RegressionStage)int.MaxValue, "MOV PA, #0"), Is.TypeOf<InvalidOperationException>());
        });
    }

    [Test]
    public void SnippetMatcher_ContainsAndCountOccurrences_WorkOnNormalizedLines()
    {
        NormalizedSourceText haystack = NormalizeMatcherSourceText(
            """
            MOV PA, #0
            ADD PA, #1
            MOV PB, #0
            ADD PB, #1
            """);

        Pattern containsPattern = CompileMatcherPattern("MOV ?1, #0");
        PatternBindings containsBindings = CreatePatternBindings();
        Pattern countPattern = CompileMatcherPattern("ADD ?, #1");
        PatternBindings countBindings = CreatePatternBindings();
        Pattern missingPattern = CompileMatcherPattern("RET");
        PatternBindings missingBindings = CreatePatternBindings();

        Assert.Multiple(() =>
        {
            Assert.That(SnippetMatcherContains(haystack, containsPattern, containsBindings), Is.True);
            Assert.That(SnippetMatcherContains(haystack, missingPattern, missingBindings), Is.False);
            Assert.That(SnippetMatcherCountOccurrences(haystack, countPattern, countBindings), Is.EqualTo(2));
            Assert.That(SnippetMatcherCountOccurrences(haystack, missingPattern, CreatePatternBindings()), Is.EqualTo(0));
        });
    }

    [Test]
    public void PatternMatching_RejectsMultiLinePatternsAndNonIdentifierWildcardMatches()
    {
        Pattern pattern = CompileMatcherPattern("MOV ?");
        NormalizedText line = NormalizeMatcherLine("MOV ,");
        PatternBindings bindings = CreatePatternBindings();

        Assert.Multiple(() =>
        {
            Assert.That(PatternTryMatchLine(pattern, line, bindings), Is.False);
            Assert.That(AssertThrowsFromCompileMatcherPattern("MOV\n?"), Is.TypeOf<ArgumentException>());
        });
    }

    [Test]
    public void ArtifactWriter_WritesMatcherTraceArtifact_WhenTraceReportPresent()
    {
        using TempDirectory temp = new();

        object traceReport = EvaluateMatcherTraceReport(
            """
            MOV PA, #0
            ADD PA, #1
            MOV PB, #0
            ADD PB, #1
            NOP
            RET
            """);

        object evaluatedFixture = CreateEvaluatedFixtureWithMatcherTrace("Demonstrators/matcher_trace.blade", traceReport);
        object writer = CreateArtifactWriter(temp.Path, enabled: true);
        RegressionFixture fixture = new(
            temp.GetFullPath("Demonstrators/matcher_trace.blade"),
            "Demonstrators/matcher_trace.blade",
            RegressionFixtureKind.Blade,
            string.Empty,
            string.Empty,
            CreateMatcherTraceExpectation());

        string artifactDirectoryPath = InvokeArtifactWriter(
            writer,
            fixture,
            evaluatedFixture,
            "failed",
            ["failed"]);
        string matcherTracePath = Path.Combine(artifactDirectoryPath, "matcher-trace.txt");
        string matcherTraceText = File.ReadAllText(matcherTracePath);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(matcherTracePath), Is.True);
            Assert.That(matcherTraceText, Does.Contain("CONTAINS block 1: FAIL"));
            Assert.That(matcherTraceText, Does.Contain("?1 = PA"));
        });
    }

    [Test]
    public void RegressionJsonFormatter_EmitsCamelCaseEnumStrings()
    {
        RegressionFixtureResult fixtureResult = new(
            "Demonstrators/hw.blade",
            RegressionFixtureOutcome.HwErr,
            "failed",
            ["detail"],
            "/repo/.artifacts/regressions/run/fail",
            true);
        RegressionRunResult result = new("/repo", [fixtureResult]);

        using JsonDocument document = JsonDocument.Parse(RegressionJsonFormatter.Format(result));
        JsonElement fixture = document.RootElement.GetProperty("fixtureResults")[0];

        Assert.Multiple(() =>
        {
            Assert.That(document.RootElement.GetProperty("succeeded").GetBoolean(), Is.False);
            Assert.That(fixture.GetProperty("outcome").GetString(), Is.EqualTo("hwErr"));
            Assert.That(fixture.GetProperty("hardwareAttempted").GetBoolean(), Is.True);
            Assert.That(fixture.GetProperty("artifactDirectoryPath").GetString(), Is.EqualTo("/repo/.artifacts/regressions/run/fail"));
        });
    }

    [Test]
    public void RegressionRunner_Filter_CanSelectSingleFixture()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/one.blade", "fn one() -> u32 { return 1; }");
        temp.WriteFile("Demonstrators/two.blade", "fn two() -> u32 { return 2; }");

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            Filters = ["Demonstrators/two.blade"],
        });

        Assert.That(result.FixtureResults.Select(item => item.RelativePath), Is.EqualTo(["Demonstrators/two.blade"]));
    }

    [Test]
    public void HardwareOutputMismatchMessage_FormatsHexUnsignedAndSignedValues()
    {
        MethodInfo formatter = typeof(RegressionRunner).GetMethod(
            "FormatHardwareOutputMismatch",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        string message = (string)formatter.Invoke(null, new object[]
        {
            new uint[] { 0x0000012Cu, 0, 0, 0, 0, 0, 0, 0 },
            new uint[] { 0x000000C8u, 0, 0, 0, 0, 0, 0, 0 },
        })!;

        Assert.That(
            message,
            Is.EqualTo(
                """
                hardware output mismatch:
                  expected:
                    [0] 0x0000012C | unsigned 300 | signed 300
                    [1] 0x00000000 | unsigned 0 | signed 0
                    [2] 0x00000000 | unsigned 0 | signed 0
                    [3] 0x00000000 | unsigned 0 | signed 0
                    [4] 0x00000000 | unsigned 0 | signed 0
                    [5] 0x00000000 | unsigned 0 | signed 0
                    [6] 0x00000000 | unsigned 0 | signed 0
                    [7] 0x00000000 | unsigned 0 | signed 0
                  actual:
                    [0] 0x000000C8 | unsigned 200 | signed 200
                    [1] 0x00000000 | unsigned 0 | signed 0
                    [2] 0x00000000 | unsigned 0 | signed 0
                    [3] 0x00000000 | unsigned 0 | signed 0
                    [4] 0x00000000 | unsigned 0 | signed 0
                    [5] 0x00000000 | unsigned 0 | signed 0
                    [6] 0x00000000 | unsigned 0 | signed 0
                    [7] 0x00000000 | unsigned 0 | signed 0
                
                """));
    }

    [Test]
    public void PassHwFixture_RunsDirective_ParsesScalarAndArrayOutputs()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        WriteHardwareRuntime(temp);
        temp.WriteFile("Demonstrators/hw_mixed_runs.blade", """
        // EXPECT: pass-hw
        // RUNS:
        // - [] = 1234
        // - [ 0 ] = [ 1234 ]
        // - [ 0, -10, 0x12345 ] = [ -1, 0x10, 0 ]
        cog task main {
            var x: u32 = 1234;
            _ = x;
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            HardwarePort = "",  // disable hardware; test only verifies RUNS parsing
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(result =>
            result.RelativePath == "Demonstrators/hw_mixed_runs.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Ok));
        });
    }

    [Test]
    public void PassHwFixture_RunsDirective_RejectsMoreThanEightExpectedOutputs()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/hw_too_many_expected_outputs.blade", """
        // EXPECT: pass-hw
        // RUNS:
        // - [ 0 ] = [ 0, 1, 2, 3, 4, 5, 6, 7, 8 ]
        var x: u32 = 0;
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(run =>
            run.RelativePath == "Demonstrators/hw_too_many_expected_outputs.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Fail));
            Assert.That(fixtureResult.Details, Has.Some.Contains("Hardware fixtures support at most 8 expected outputs."));
        });
    }

    [Test]
    public void PassHwFixture_RunsDirective_RejectsMoreThanEightParameters()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/hw_too_many_runs.blade", """
        // EXPECT: pass-hw
        // RUNS:
        // - [ 0, 1, 2, 3, 4, 5, 6, 7, 8 ] = 0
        var x: u32 = 0;
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(result =>
            result.RelativePath == "Demonstrators/hw_too_many_runs.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Fail));
            Assert.That(fixtureResult.Details, Has.Some.Contains("Hardware fixtures support at most 8 parameters."));
        });
    }

    [Test]
    public void PassHwFixture_RunsDirective_RejectsInvalidLiteral()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/hw_invalid_literal.blade", """
        // EXPECT: pass-hw
        // RUNS:
        // - [ 0b10 ] = 0
        var x: u32 = 0;
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(result =>
            result.RelativePath == "Demonstrators/hw_invalid_literal.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Fail));
            Assert.That(fixtureResult.Details, Has.Some.Contains("Invalid hardware literal '0b10'."));
        });
    }

    [Test]
    public void PassHwFixture_RunsDirective_RejectsOverflowLiteral()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/hw_overflow_literal.blade", """
        // EXPECT: pass-hw
        // RUNS:
        // - [ 4294967296 ] = 0
        var x: u32 = 0;
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(result =>
            result.RelativePath == "Demonstrators/hw_overflow_literal.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Fail));
            Assert.That(fixtureResult.Details, Has.Some.Contains("Invalid hardware literal '4294967296'."));
        });
    }

    [Test]
    public void PassHwFixture_RunsDirective_RejectsInvalidEntryShape()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/hw_invalid_entry_shape.blade", """
        // EXPECT: pass-hw
        // RUNS:
        // - 0, 1 = 2
        var x: u32 = 0;
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(result =>
            result.RelativePath == "Demonstrators/hw_invalid_entry_shape.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Fail));
            Assert.That(fixtureResult.Details, Has.Some.Contains("Invalid RUNS entry '0, 1 = 2'. Expected '[ ... ] = value' or '[ ... ] = [ ... ]'."));
        });
    }

    [Test]
    public void XFailHwFixture_WithoutHardwarePort_IsPass()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        WriteHardwareRuntime(temp);
        temp.WriteFile("Demonstrators/xfailhw_no_port.blade", """
        // EXPECT: xfail-hw
        // RUNS:
        // - [] = 0x1
        cog task main {
            var x: u32 = 1;
            _ = x;
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            HardwarePort = "",  // disable hardware; xfail-hw falls back to compile-side pass semantics
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(r =>
            r.RelativePath == "Demonstrators/xfailhw_no_port.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Ok));
        });
    }

    [Test]
    public void XFailHwFixture_RequiresRunsDirective()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/xfailhw_no_runs.blade", """
        // EXPECT: xfail-hw
        var x: u32 = 0;
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(r =>
            r.RelativePath == "Demonstrators/xfailhw_no_runs.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Fail));
            Assert.That(fixtureResult.Details, Has.Some.Contains("EXPECT: xfail-hw requires RUNS."));
        });
    }

    [Test]
    public void XFailFixture_WithMatchingDiagnostics_IsXFail()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.MakeDir("Demonstrators/Binder");
        temp.WriteFile("Demonstrators/Binder/fail_control_flow_contexts.blade", """
        // EXPECT: xfail
        // DIAGNOSTICS: UndefinedName
        cog task main {
            missing();
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(r =>
            r.RelativePath == "Demonstrators/Binder/fail_control_flow_contexts.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.XFail));
            Assert.That(fixtureResult.Summary, Is.EqualTo("failed as expected"));
            Assert.That(fixtureResult.Details, Has.Some.Contains("expected compilation to succeed, but it produced error diagnostics"));
        });
    }

    [Test]
    public void XFailFixture_WhenExpectedDiagnosticsDisappear_IsXFail()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/xfail_resolved.blade", """
        // EXPECT: xfail
        // DIAGNOSTICS: UndefinedName
        cog task main {
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(r =>
            r.RelativePath == "Demonstrators/xfail_resolved.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.XFail));
            Assert.That(fixtureResult.Summary, Is.EqualTo("failed as expected"));
            Assert.That(fixtureResult.Details, Has.Some.Contains("missing diagnostic UndefinedName: expected at least 1, got 0"));
        });
    }

    [Test]
    public void XPassFixture_WhenCompilationUnexpectedlySucceeds_IsXPass()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/xpass_accepts.blade", """
        // EXPECT: xpass
        // DIAGNOSTICS: UndefinedName
        cog task main {
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(r =>
            r.RelativePath == "Demonstrators/xpass_accepts.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.XPass));
            Assert.That(fixtureResult.Summary, Is.EqualTo("did not fail as expected"));
            Assert.That(fixtureResult.Details, Has.Some.Contains("expected compilation to fail, but it completed without error diagnostics"));
        });
    }

    [Test]
    public void XPassFixture_WhenExpectedFailureStillOccurs_IsUnexpected()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/xpass_still_fails.blade", """
        // EXPECT: xpass
        // DIAGNOSTICS: UndefinedName
        cog task main {
            missing();
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(r =>
            r.RelativePath == "Demonstrators/xpass_still_fails.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Fail));
            Assert.That(fixtureResult.Summary, Is.EqualTo("did not meet expectations"));
        });
    }

    [Test]
    public void HeaderValidation_RequiresExpectOnFirstLine()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/header_expect_line_two.blade", """

        // EXPECT: pass
        var x: u32 = 0;
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(result =>
            result.RelativePath == "Demonstrators/header_expect_line_two.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Fail));
            Assert.That(fixtureResult.Details, Has.Some.Contains("EXPECT must be the first line of the file."));
        });
    }

    [Test]
    public void HeaderValidation_RejectsPlainCommentOutsideNoteBlock()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/header_plain_comment.blade", """
        // EXPECT: pass
        // plain comment
        var x: u32 = 0;
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(result =>
            result.RelativePath == "Demonstrators/header_plain_comment.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Fail));
            Assert.That(fixtureResult.Details, Has.Some.Contains("Header comments after EXPECT must use a supported directive or NOTE block."));
        });
    }

    [Test]
    public void HeaderValidation_RejectsUnknownDirective()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/header_unknown_directive.blade", """
        // EXPECT: pass
        // TODO: move this into NOTE
        var x: u32 = 0;
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(result =>
            result.RelativePath == "Demonstrators/header_unknown_directive.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Fail));
            Assert.That(fixtureResult.Details, Has.Some.Contains("Unsupported header directive 'TODO'."));
        });
    }

    [Test]
    public void HeaderValidation_RejectsFailWithoutDiagnosticExpectation()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/fail_without_diagnostics.blade", """
        // EXPECT: fail
        cog task main {
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            Filters = ["fail_without_diagnostics.blade"],
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Fail));
            Assert.That(fixtureResult.Details, Has.Some.Contains("EXPECT: fail requires at least one DIAGNOSTICS expectation."));
        });
    }

    [Test]
    public void HeaderValidation_RejectsXPassWithoutDiagnosticExpectation()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/xpass_without_diagnostics.blade", """
        // EXPECT: xpass
        cog task main {
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            Filters = ["xpass_without_diagnostics.blade"],
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Fail));
            Assert.That(fixtureResult.Details, Has.Some.Contains("EXPECT: xpass requires at least one DIAGNOSTICS expectation."));
        });
    }

    [Test]
    public void CodeAssertions_ContainsBlocksHaveIndependentWildcardBindings()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/contains_independent_bindings.blade", """
        // EXPECT: pass
        // STAGE: final-asm
        // FLEXSPIN: forbidden
        // CONTAINS:
        // - MOV ?1, #1
        // - ADD ?1, #2
        // CONTAINS:
        // - MOV ?1, #3
        // - ADD ?1, #4
        cog task main {
            asm volatile {
                MOV PA, #1
                ADD PA, #2
                MOV PB, #3
                ADD PB, #4
            };
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            Filters = ["contains_independent_bindings.blade"],
        });

        Assert.That(result.Succeeded, Is.True, RegressionReportFormatter.Format(result));
    }

    [Test]
    public void CodeAssertions_SequenceBlocksHaveIndependentWildcardBindings()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/sequence_independent_bindings.blade", """
        // EXPECT: pass
        // STAGE: final-asm
        // FLEXSPIN: forbidden
        // SEQUENCE:
        // - MOV ?1, #1
        // - ADD ?1, #2
        // SEQUENCE:
        // - MOV ?1, #3
        // - ADD ?1, #4
        cog task main {
            asm volatile {
                MOV PA, #1
                ADD PA, #2
                MOV PB, #3
                ADD PB, #4
            };
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            Filters = ["sequence_independent_bindings.blade"],
        });

        Assert.That(result.Succeeded, Is.True, RegressionReportFormatter.Format(result));
    }

    [Test]
    public void CodeAssertions_ExactRejectsInterleavedUnexpectedTextButAllowsOuterText()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/exact_allows_outer_text.blade", """
        // EXPECT: pass
        // STAGE: final-asm
        // FLEXSPIN: forbidden
        // EXACT:
        // - MOV PA, #1
        // - NOP
        // - ADD PA, #2
        cog task main {
            asm volatile {
                MOV PA, #1
                NOP
                ADD PA, #2
            };
        }
        """);
        temp.WriteFile("Demonstrators/exact_rejects_interleaved_text.blade", """
        // EXPECT: pass
        // STAGE: final-asm
        // FLEXSPIN: forbidden
        // EXACT:
        // - MOV PA, #1
        // - ADD PA, #2
        cog task main {
            asm volatile {
                MOV PA, #1
                NOP
                ADD PA, #2
            };
        }
        """);

        RegressionRunResult passingResult = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            Filters = ["exact_allows_outer_text.blade"],
        });
        RegressionRunResult failingResult = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            Filters = ["exact_rejects_interleaved_text.blade"],
        });

        RegressionFixtureResult failingFixture = failingResult.FixtureResults.Single();
        Assert.Multiple(() =>
        {
            Assert.That(passingResult.Succeeded, Is.True, RegressionReportFormatter.Format(passingResult));
            Assert.That(failingResult.Succeeded, Is.False);
            Assert.That(failingFixture.Details, Has.Some.Contains("unexpected text between exact snippets before: ADD PA, #2"));
        });
    }

    [Test]
    public void CodeAssertions_SequenceNegativesCheckPrefixSuffixAndRejectOnlyNegativeBlocks()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/sequence_negative_edges.blade", """
        // EXPECT: pass
        // STAGE: final-asm
        // FLEXSPIN: forbidden
        // SEQUENCE:
        // ! WAITX
        // - MOV PA, #1
        // ! WAITX
        cog task main {
            asm volatile {
                MOV PA, #1
            };
        }
        """);
        temp.WriteFile("Demonstrators/sequence_only_negative.blade", """
        // EXPECT: pass
        // STAGE: final-asm
        // SEQUENCE:
        // ! NOP
        cog task main {
        }
        """);
        temp.WriteFile("Demonstrators/exact_negative_edges.blade", """
        // EXPECT: pass
        // STAGE: final-asm
        // FLEXSPIN: forbidden
        // EXACT:
        // ! WAITX
        // - MOV PA, #1
        // ! WAITX
        cog task main {
            asm volatile {
                MOV PA, #1
            };
        }
        """);
        temp.WriteFile("Demonstrators/exact_only_negative.blade", """
        // EXPECT: pass
        // STAGE: final-asm
        // EXACT:
        // ! NOP
        cog task main {
        }
        """);

        RegressionRunResult passingResult = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            Filters = ["sequence_negative_edges.blade"],
        });
        RegressionRunResult exactPassingResult = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            Filters = ["exact_negative_edges.blade"],
        });
        RegressionRunResult failingResult = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            Filters = ["sequence_only_negative.blade"],
        });
        RegressionRunResult exactFailingResult = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            Filters = ["exact_only_negative.blade"],
        });

        RegressionFixtureResult failingFixture = failingResult.FixtureResults.Single();
        RegressionFixtureResult exactFailingFixture = exactFailingResult.FixtureResults.Single();
        Assert.Multiple(() =>
        {
            Assert.That(passingResult.Succeeded, Is.True, RegressionReportFormatter.Format(passingResult));
            Assert.That(exactPassingResult.Succeeded, Is.True, RegressionReportFormatter.Format(exactPassingResult));
            Assert.That(failingResult.Succeeded, Is.False);
            Assert.That(exactFailingResult.Succeeded, Is.False);
            Assert.That(failingFixture.Details, Has.Some.Contains("SEQUENCE block requires at least one '-' or count item."));
            Assert.That(exactFailingFixture.Details, Has.Some.Contains("EXACT block requires at least one '-' or count item."));
        });
    }

    [Test]
    public void CodeAssertions_RejectsZeroCountAndAcceptsPositiveCount()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/count_positive.blade", """
        // EXPECT: pass
        // STAGE: final-asm
        // FLEXSPIN: forbidden
        // CONTAINS:
        // 3x NOP
        cog task main {
            asm volatile {
                NOP
                NOP
            };
        }
        """);
        temp.WriteFile("Demonstrators/count_zero.blade", """
        // EXPECT: pass
        // STAGE: final-asm
        // CONTAINS:
        // 0x NOP
        cog task main {
        }
        """);

        RegressionRunResult passingResult = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            Filters = ["count_positive.blade"],
        });
        RegressionRunResult failingResult = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            Filters = ["count_zero.blade"],
        });

        RegressionFixtureResult failingFixture = failingResult.FixtureResults.Single();
        Assert.Multiple(() =>
        {
            Assert.That(passingResult.Succeeded, Is.True, RegressionReportFormatter.Format(passingResult));
            Assert.That(failingResult.Succeeded, Is.False);
            Assert.That(failingFixture.Details, Has.Some.Contains("CONTAINS count prefixes must be greater than zero. Use '!' for negative assertions."));
        });
    }

    [Test]
    public void CodeAssertions_WildcardsDoNotFuseIdentifiers()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/wildcard_identifier_fusion.blade", """
        // EXPECT: pass
        // STAGE: final-asm
        // FLEXSPIN: forbidden
        // SEQUENCE:
        // - ANDN ?1, #10
        // - AND ?1, #20
        cog task main {
            asm volatile {
                FOO:
                NFOO:
                ANDN FOO, #10
                AND NFOO, #20
            };
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            Filters = ["wildcard_identifier_fusion.blade"],
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Details, Has.Some.Contains("missing ordered snippet: Pattern { Source = AND ?1, #20"));
        });
    }

    [Test]
    public void HeaderValidation_BlankLineTerminatesExpectationBlock()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/header_blank_line_terminates.blade", """
        // EXPECT: pass
        // DIAGNOSTICS:
        // - MainTaskMustBeCog

        // This comment documents the fixture and must not be parsed as a header directive.
        hub task main {
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            Filters = ["header_blank_line_terminates.blade"],
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Ok));
            Assert.That(fixtureResult.Summary, Is.EqualTo("ok"));
        });
    }

    [Test]
    public void HeaderValidation_WhitespaceOnlyLineTerminatesExpectationBlock()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile(
            "Demonstrators/header_whitespace_line_terminates.blade",
            "// EXPECT: pass\n"
            + "// DIAGNOSTICS:\n"
            + "// - MainTaskMustBeCog\n"
            + " \t \n"
            + "// This comment documents the fixture and must not be parsed as a header directive.\n"
            + "lut task main {\n"
            + "}\n");

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            Filters = ["header_whitespace_line_terminates.blade"],
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Ok));
            Assert.That(fixtureResult.Summary, Is.EqualTo("ok"));
        });
    }

    [Test]
    public void BladeCrashFixture_PassesWhenCompilationProducesDiagnosticsButDoesNotThrow()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("RegressionTests/syntax_failure.blade.crash", "fn main(");

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            Filters = ["syntax_failure.blade.crash"],
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(fixtureResult.RelativePath, Is.EqualTo("RegressionTests/syntax_failure.blade.crash"));
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Ok));
            Assert.That(fixtureResult.Summary, Is.EqualTo("ok"));
            Assert.That(fixtureResult.Details, Is.Empty);
        });
    }

    [Test]
    public void BladeCrashFixture_PassesWhenSourceIsInvalidUtf8ButCompilerDoesNotThrow()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("RegressionTests/invalid_utf8.blade.crash", new byte[] { 0x80, 0x61 });

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            Filters = ["invalid_utf8.blade.crash"],
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(fixtureResult.RelativePath, Is.EqualTo("RegressionTests/invalid_utf8.blade.crash"));
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Ok));
            Assert.That(fixtureResult.Summary, Is.EqualTo("ok"));
        });
    }

    [Test]
    public void FullRegressionSuite_WithIrGuard_MovesObservedTypesToCovered()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        WriteIrCoverageGuard(temp, """
        {
            "bound": {
                "covered": [],
                "uncovered": ["BoundModule", "BoundProgram"]
            },
            "mir": {
                "covered": [],
                "uncovered": []
            },
            "lir": {
                "covered": [],
                "uncovered": []
            },
            "asmir": {
                "covered": [],
                "uncovered": []
            }
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.IrCoverageReport, Is.Not.Null);
        Assert.That(ReadGuardArray(temp, "bound", "covered"), Does.Contain("BoundModule"));
        Assert.That(ReadGuardArray(temp, "bound", "covered"), Does.Contain("BoundProgram"));
        Assert.That(ReadGuardArray(temp, "bound", "uncovered"), Does.Not.Contain("BoundModule"));
        Assert.That(ReadGuardArray(temp, "bound", "uncovered"), Does.Not.Contain("BoundProgram"));
    }

    [Test]
    public void FullRegressionSuite_WithIrGuard_PassesArrayLiteralInferenceFixture()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.MakeDir("Demonstrators/Language");
        temp.WriteFile("Demonstrators/Language/pass_array_literal_inference.bound.blade", """
        // EXPECT: pass
        // STAGE: bound
        // CONTAINS:
        // - ArrayLit<[3]<int-literal>>
        cog task main {
            _ = [1, 2, 3];
        }
        """);
        WriteIrCoverageGuard(temp, """
        {
            "bound": {
                "covered": [],
                "uncovered": []
            },
            "mir": {
                "covered": [],
                "uncovered": []
            },
            "lir": {
                "covered": [],
                "uncovered": []
            },
            "asmir": {
                "covered": [],
                "uncovered": []
            }
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(static fixture => fixture.RelativePath == "Demonstrators/Language/pass_array_literal_inference.bound.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.IrCoverageReport, Is.Not.Null);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Ok));
            Assert.That(fixtureResult.Summary, Is.EqualTo("ok"));
        });
    }

    [Test]
    public void FullRegressionSuite_WithIrGuard_ReportsCoverageRegressions()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        WriteIrCoverageGuard(temp, """
        {
            "bound": {
                "covered": [],
                "uncovered": []
            },
            "mir": {
                "covered": ["MirInlineAsmInstruction"],
                "uncovered": []
            },
            "lir": {
                "covered": [],
                "uncovered": []
            },
            "asmir": {
                "covered": [],
                "uncovered": []
            }
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.IrCoverageReport, Is.Not.Null);
        Assert.That(
            result.IrCoverageReport!.RegressionMessages,
            Does.Contain("regression detected: MirInlineAsmInstruction is not covered by the regression suite anymore"));
        Assert.That(
            RegressionReportFormatter.Format(result),
            Does.Contain("IR coverage regressions:" + Environment.NewLine + "  regression detected: MirInlineAsmInstruction is not covered by the regression suite anymore"));
        Assert.That(ReadGuardArray(temp, "mir", "covered"), Does.Contain("MirInlineAsmInstruction"));
    }

    [Test]
    public void FullRegressionSuite_WithIrGuard_PrintsCurrentUncoveredTypes()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        WriteIrCoverageGuard(temp, """
        {
            "bound": {
                "covered": [],
                "uncovered": []
            },
            "mir": {
                "covered": [],
                "uncovered": []
            },
            "lir": {
                "covered": [],
                "uncovered": []
            },
            "asmir": {
                "covered": [],
                "uncovered": []
            }
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.IrCoverageReport, Is.Not.Null);
        string report = RegressionReportFormatter.Format(result);
        Assert.That(report, Does.Contain("uncovered Bound Nodes:"));
        Assert.That(report, Does.Contain("uncovered MIR Nodes:"));
        Assert.That(report, Does.Contain("uncovered LIR Nodes:"));
        Assert.That(report, Does.Contain("uncovered ASMIR Nodes:"));
    }

    private static void WriteMinimalRegressionRepository(TempDirectory temp)
    {
        temp.MakeDir("Examples");
        temp.MakeDir("Demonstrators");
        temp.MakeDir("Blade.Tests");
        temp.MakeDir("Blade.Tests/Reject");
        temp.MakeDir("Blade");
        temp.MakeDir("RegressionTests");
        temp.WriteFile("justfile", "fuzz:\n    false\n");
        temp.WriteFile("Examples/smoke.blade", "cog task main { }");
        WriteRegressionConfig(temp);
    }

    private static RegressionExpectation CreateMatcherTraceExpectation()
    {
        return new RegressionExpectation(
            RegressionExpectationKind.Pass,
            RegressionStage.FinalAsm,
            [
                new SnippetBlock(
                [
                    CreatePositiveSnippetItem("MOV ?1, #0"),
                    CreateExactCountSnippetItem("ADD ?1, #1", 3),
                ]),
            ],
            [
                new SnippetBlock(
                [
                    CreatePositiveSnippetItem("MOV ?1, #0"),
                    CreatePositiveSnippetItem("ADD ?1, #1"),
                ]),
                new SnippetBlock(
                [
                    CreatePositiveSnippetItem("MOV PA, #0"),
                    CreateNegativeSnippetItem("MOV PB, #0"),
                    CreatePositiveSnippetItem("RET"),
                ]),
                new SnippetBlock(
                [
                    CreatePositiveSnippetItem("MOV PB, #0"),
                    CreatePositiveSnippetItem("JMP #done"),
                ]),
            ],
            [
                new SnippetBlock(
                [
                    CreatePositiveSnippetItem("MOV PA, #0"),
                    CreatePositiveSnippetItem("RET"),
                ]),
            ],
            [],
            [],
            FlexspinExpectation.Forbidden,
            [],
            []);
    }

    private static SnippetItem CreatePositiveSnippetItem(string text)
    {
        return SnippetItem.Positive(CompileSnippetPattern(text));
    }

    private static SnippetItem CreateNegativeSnippetItem(string text)
    {
        return SnippetItem.Negative(CompileSnippetPattern(text));
    }

    private static SnippetItem CreateExactCountSnippetItem(string text, int count)
    {
        return SnippetItem.ExactCount(CompileSnippetPattern(text), count);
    }

    private static Pattern CompileSnippetPattern(string text)
    {
        return Pattern.Compile(text);
    }

    private static object EvaluateMatcherTraceReport(string rawText)
    {
        return EvaluateMatcherTrace(CreateMatcherTraceExpectation(), rawText).TraceReport;
    }

    private static NormalizedSourceText NormalizeMatcherSourceText(string rawText)
    {
        return CodeNormalizer.NormalizeSourceText(rawText);
    }

    private static NormalizedSourceText NormalizeBladeStage(RegressionStage stage, string rawText)
    {
        return CodeNormalizer.NormalizeBladeStage(stage, rawText);
    }

    private static NormalizedSourceText NormalizeAssemblyText(string rawText)
    {
        return NormalizeBladeStage(RegressionStage.FinalAsm, rawText);
    }

    private static NormalizedText NormalizeMatcherLine(string rawText)
    {
        return CodeNormalizer.NormalizeText(rawText);
    }

    private static Exception AssertThrowsFromNormalizeMatcherLine(string rawText)
    {
        try
        {
            _ = NormalizeMatcherLine(rawText);
            throw new AssertionException("NormalizeMatcherLine was expected to throw.");
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Exception AssertThrowsFromCompileMatcherPattern(string rawText)
    {
        try
        {
            _ = CompileMatcherPattern(rawText);
            throw new AssertionException("CompileMatcherPattern was expected to throw.");
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Exception AssertThrowsFromNormalizeBladeStage(RegressionStage stage, string rawText)
    {
        try
        {
            _ = NormalizeBladeStage(stage, rawText);
            throw new AssertionException("NormalizeBladeStage was expected to throw.");
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static IReadOnlyList<string> GetNormalizedSegments(NormalizedText normalizedText)
    {
        return normalizedText.Segments;
    }

    private static IReadOnlyList<string> GetNormalizedLineTexts(NormalizedSourceText normalizedSourceText)
    {
        return GetNormalizedLines(normalizedSourceText)
            .Select(GetNormalizedLineText)
            .ToArray();
    }

    private static IReadOnlyList<int> GetNormalizedSourceLineNumbers(NormalizedSourceText normalizedSourceText)
    {
        return GetNormalizedLines(normalizedSourceText)
            .Select(GetNormalizedSourceLineNumber)
            .ToArray();
    }

    private static string GetNormalizedGapText(NormalizedSourceText normalizedSourceText, int startLineIndex, int endLineIndex)
    {
        return normalizedSourceText.GetGapText(startLineIndex, endLineIndex);
    }

    private static Pattern CompileMatcherPattern(string text)
    {
        return Pattern.Compile(text);
    }

    private static PatternBindings CreatePatternBindings()
    {
        return new PatternBindings();
    }

    private static bool SnippetMatcherContains(NormalizedSourceText haystack, Pattern pattern, PatternBindings bindings)
    {
        return SnippetMatcher.Contains(haystack, pattern, bindings);
    }

    private static int SnippetMatcherCountOccurrences(NormalizedSourceText haystack, Pattern pattern, PatternBindings bindings)
    {
        return SnippetMatcher.CountOccurrences(haystack, pattern, bindings);
    }

    private static bool PatternTryMatchLine(Pattern pattern, NormalizedText normalizedLine, PatternBindings bindings)
    {
        return pattern.TryMatchLine(normalizedLine, bindings);
    }

    private static IReadOnlyList<NormalizedSourceLine> GetNormalizedLines(NormalizedSourceText normalizedSourceText)
    {
        return normalizedSourceText.Lines;
    }

    private static string GetNormalizedLineText(NormalizedSourceLine normalizedSourceLine)
    {
        return normalizedSourceLine.Text.Text;
    }

    private static int GetNormalizedSourceLineNumber(NormalizedSourceLine normalizedSourceLine)
    {
        return normalizedSourceLine.SourceLineNumber;
    }

    private static string RenderMatcherTraceReport(object traceReport)
    {
        Type formatterType = typeof(RegressionRunner).Assembly.GetType("Blade.Regressions.MatcherTraceFormatter")
            ?? throw new InvalidOperationException("MatcherTraceFormatter type not found.");
        MethodInfo formatMethod = formatterType.GetMethod(
            "Format",
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("MatcherTraceFormatter.Format method not found.");
        return (string)formatMethod.Invoke(null, [traceReport])!;
    }

    private static CodeAssertionTestResult EvaluateMatcherTrace(RegressionExpectation expectation, string rawText)
    {
        NormalizedSourceText normalizedText = NormalizeMatcherSourceText(rawText);
        MethodInfo method = typeof(RegressionRunner).GetMethod(
            "EvaluateNormalizedAssertions",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        object evaluationResult = method.Invoke(null, [expectation, normalizedText, RegressionStage.FinalAsm])!;
        PropertyInfo issuesProperty = evaluationResult.GetType().GetProperty("Issues")!;
        PropertyInfo matcherTraceReportProperty = evaluationResult.GetType().GetProperty("MatcherTraceReport")!;
        IReadOnlyList<string> issues = (IReadOnlyList<string>)(issuesProperty.GetValue(evaluationResult)
            ?? throw new InvalidOperationException("Issues were null."));
        object traceReport = matcherTraceReportProperty.GetValue(evaluationResult)
            ?? throw new InvalidOperationException("MatcherTraceReport was null.");
        return new CodeAssertionTestResult(issues, traceReport, RenderMatcherTraceReport(traceReport));
    }

    private static object CreateEvaluatedFixtureWithMatcherTrace(string relativePath, object traceReport)
    {
        Type evaluatedFixtureType = typeof(RegressionRunner).Assembly.GetType("Blade.Regressions.EvaluatedFixture")
            ?? throw new InvalidOperationException("EvaluatedFixture type not found.");
        MethodInfo emptyMethod = evaluatedFixtureType.GetMethod(
            "Empty",
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("EvaluatedFixture.Empty method not found.");
        object emptyFixture = emptyMethod.Invoke(null, [relativePath])
            ?? throw new InvalidOperationException("EvaluatedFixture.Empty returned null.");
        MethodInfo withMatcherTraceMethod = evaluatedFixtureType.GetMethod(
            "WithMatcherTrace",
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("EvaluatedFixture.WithMatcherTrace method not found.");
        return withMatcherTraceMethod.Invoke(emptyFixture, [traceReport])
            ?? throw new InvalidOperationException("EvaluatedFixture.WithMatcherTrace returned null.");
    }

    private static object CreateArtifactWriter(string repositoryRootPath, bool enabled)
    {
        Type artifactWriterType = typeof(RegressionRunner).Assembly.GetType("Blade.Regressions.ArtifactWriter")
            ?? throw new InvalidOperationException("ArtifactWriter type not found.");
        return Activator.CreateInstance(artifactWriterType, repositoryRootPath, enabled)
            ?? throw new InvalidOperationException("ArtifactWriter instance creation failed.");
    }

    private static string InvokeArtifactWriter(
        object writer,
        RegressionFixture fixture,
        object evaluatedFixture,
        string summary,
        IReadOnlyList<string> issues)
    {
        MethodInfo method = writer.GetType().GetMethod("WriteFailureArtifacts")
            ?? throw new InvalidOperationException("ArtifactWriter.WriteFailureArtifacts method not found.");
        return (string?)method.Invoke(writer, [fixture, evaluatedFixture, summary, issues, null])
            ?? throw new InvalidOperationException("ArtifactWriter.WriteFailureArtifacts returned null.");
    }

    private static void WriteHardwareRuntime(TempDirectory temp)
    {
        temp.WriteFile("Blade.HwTestRunner/Runtime.blade", """
        import builtin;

        layout Runtime {
            extern cog var rt_param0: u32 @(1);
            extern cog var rt_param1: u32 @(2);
            extern cog var rt_param2: u32 @(3);
            extern cog var rt_param3: u32 @(4);
            extern cog var rt_param4: u32 @(5);
            extern cog var rt_param5: u32 @(6);
            extern cog var rt_param6: u32 @(7);
            extern cog var rt_param7: u32 @(8);
            cog var rt_result: u32 @(0x1EF) = 0;
        }

        cog task _start : Runtime {
            builtin.init_memory();
            builtin.task_main();
        }
        """);
        WriteRegressionConfig(temp);
    }

    private static void WriteIrCoverageGuard(TempDirectory temp, string content)
    {
        temp.WriteFile("RegressionTests/ir-regression-guard.json", content);
        WriteRegressionConfig(temp);
    }

    private static void WriteRegressionConfig(TempDirectory temp)
    {
        bool hasHardwareRuntime = File.Exists(Path.Combine(temp.Path, "Blade.HwTestRunner", "Runtime.blade"));
        bool hasIrCoverageGuard = File.Exists(Path.Combine(temp.Path, "RegressionTests", "ir-regression-guard.json"));

        string poolsProperty = """
    "pools": [
        { "path": "Examples", "expect": "accept" },
        { "path": "Demonstrators", "expect": "encoded" },
        { "path": "RegressionTests", "expect": "encoded" },
        { "path": "Blade.Tests/Reject", "expect": "reject" }
    ]
""";

        List<string> properties = [poolsProperty];
        if (hasHardwareRuntime)
            properties.Add("    \"hardwareRuntimePath\": \"Blade.HwTestRunner/Runtime.blade\"");
        if (hasIrCoverageGuard)
            properties.Add("    \"irCoverageGuardPath\": \"RegressionTests/ir-regression-guard.json\"");

        temp.WriteFile("regressions.cfg.json", BuildJsonObject(properties));
    }

    private static void WriteRegressionConfig(TempDirectory temp, string content)
    {
        temp.WriteFile("regressions.cfg.json", content);
    }

    private static string BuildJsonObject(IReadOnlyList<string> properties)
    {
        return "{\n"
            + string.Join(",\n", properties)
            + "\n}\n";
    }

    private static RegressionRunOptions ParseRegressionCommandLine(params string[] args)
    {
        Type commandLineType = typeof(RegressionRunner).Assembly.GetType("Blade.Regressions.RegressionCommandLine")
            ?? throw new InvalidOperationException("RegressionCommandLine type not found.");
        MethodInfo parseMethod = commandLineType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("RegressionCommandLine.Parse method not found.");
        return (RegressionRunOptions)parseMethod.Invoke(null, [args])!;
    }

    private static string[] ReadGuardArray(TempDirectory temp, string groupName, string arrayName)
    {
        using JsonDocument document = JsonDocument.Parse(temp.ReadFile("RegressionTests/ir-regression-guard.json", System.Text.Encoding.UTF8));
        return document.RootElement
            .GetProperty(groupName)
            .GetProperty(arrayName)
            .EnumerateArray()
            .Select(static element => element.GetString()!)
            .ToArray();
    }

    private static void WriteExecutable(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead
                    | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead
                    | UnixFileMode.OtherExecute);
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> previousValues = [];

        public void Set(string name, string? value)
        {
            if (!this.previousValues.ContainsKey(name))
                this.previousValues.Add(name, Environment.GetEnvironmentVariable(name));

            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach ((string name, string? value) in this.previousValues)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private sealed class CodeAssertionTestResult
    {
        public CodeAssertionTestResult(IReadOnlyList<string> issues, object traceReport, string traceText)
        {
            Issues = issues;
            TraceReport = traceReport;
            TraceText = traceText;
        }

        public IReadOnlyList<string> Issues { get; }
        public object TraceReport { get; }
        public string TraceText { get; }
    }
}
