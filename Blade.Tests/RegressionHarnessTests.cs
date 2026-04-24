using System;
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
            "RegressionTests/raw_exact.blade",
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
            artifactDirectoryPath: null);
        RegressionRunResult result = new("/repo", [failResult]);

        string report = RegressionReportFormatter.Format(result);

        Assert.That(report, Does.Contain("  Exception stack trace:"));
        Assert.That(report, Does.Contain("  System.InvalidOperationException: boom"));
        Assert.That(report, Does.Contain("     at Blade.CompilerDriver.CompileFile(String filePath)"));
        Assert.That(report, Does.Contain("     at Blade.Regressions.RegressionRunner.ExecuteBladeCrashFixture(RegressionFixture fixture)"));
    }

    [Test]
    public void RegressionReportFormatter_HwFailed_IsLabelledAndExpandedButDoesNotAffectSucceeded()
    {
        RegressionFixtureResult passResult = new(
            "Demonstrators/simple.blade",
            RegressionFixtureOutcome.Pass,
            "passed",
            [],
            artifactDirectoryPath: null);
        RegressionFixtureResult hwFailedResult = new(
            "Demonstrators/HwTest/hw_exec.blade",
            RegressionFixtureOutcome.HwFailed,
            "hardware run 1 [] failed: port not found",
            ["hardware run 1 [] failed: port not found"],
            artifactDirectoryPath: null);
        RegressionRunResult result = new("/repo", [passResult, hwFailedResult]);

        string report = RegressionReportFormatter.Format(result);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.HwFailedCount, Is.EqualTo(1));
            Assert.That(report, Does.Contain("HW FAILED"));
            Assert.That(report, Does.Contain("hardware run 1 [] failed: port not found"));
            Assert.That(report.TrimEnd(), Does.EndWith("1 hw-failed, 1 passed, 2 total"));
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
        // - rt_result LONG 0
        // - rt_param0 LONG 0
        // - rt_param1 LONG 0
        extern cog var rt_result: u32;
        extern cog var rt_param0: u32;
        extern cog var rt_param1: i32;
        cog task main {
            rt_result = rt_param0 + bitcast(u32, rt_param1);
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
            Assert.That(result.Succeeded, Is.True);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Pass));
            Assert.That(fixtureResult.Summary, Is.EqualTo("passed"));
        });
    }

    [Test]
    public void PassHwFixture_WithExplicitRuntime_KeepsExplicitRuntimeInsteadOfConfiguredRuntime()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        WriteHardwareRuntime(temp);
        temp.WriteFile("Demonstrators/custom_runtime.spin2", """
        CON
            ' <<BLADE_CON>>
        DAT
            JMP #blade_entry
        blade_halt
            REP #1, #0
            NOP
        custom_marker LONG 0
        ' <<BLADE_DAT>>
        """);
        temp.WriteFile("Demonstrators/hw_explicit_runtime.blade", """
        // EXPECT: pass-hw
        // RUNS:
        // - [] = 0x0
        // ARGS: --runtime=custom_runtime.spin2
        // STAGE: final-asm
        // CONTAINS:
        // - custom_marker LONG 0
        // ! rt_result LONG 0
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
            Assert.That(result.Succeeded, Is.True);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Pass));
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
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Pass));
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
    public void PassHwFixture_WithConfiguredPort_HwRunFails_IsHwFailed()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        WriteHardwareRuntime(temp);
        temp.WriteFile("Demonstrators/hw_exec.blade", """
        // EXPECT: pass-hw
        // RUNS:
        // - [] = 0x0
        extern cog var rt_result: u32;
        cog task main {
            rt_result = 0;
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
            Assert.That(result.Succeeded, Is.True);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.HwFailed));
            Assert.That(fixtureResult.Details, Has.Some.StartsWith("hardware run 1 [] failed:"));
        });
    }

    [Test]
    public void XFailHwFixture_WithConfiguredPort_HwRunFails_IsHwFailed()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        WriteHardwareRuntime(temp);
        temp.WriteFile("Demonstrators/hw_xfail_exec.blade", """
        // EXPECT: xfail-hw
        // RUNS:
        // - [] = 0x1
        extern cog var rt_result: u32;
        cog task main {
            rt_result = 0;
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
            Assert.That(result.Succeeded, Is.True);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.HwFailed));
            Assert.That(fixtureResult.Details, Has.Some.StartsWith("hardware run 1 [] failed:"));
        });
    }

    [Test]
    public void HwFailedFixture_WritesArtifactsWhenEnabled()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        WriteHardwareRuntime(temp);
        temp.WriteFile("Demonstrators/hw_artifacts.blade", """
        // EXPECT: pass-hw
        // RUNS:
        // - [] = 0x0
        extern cog var rt_result: u32;
        cog task main {
            rt_result = 0;
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
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.HwFailed));
            Assert.That(fixtureResult.HardwareAttempted, Is.True);
            Assert.That(fixtureResult.ArtifactDirectoryPath, Is.Not.Null);
            Assert.That(File.Exists(Path.Combine(fixtureResult.ArtifactDirectoryPath!, "issues.txt")), Is.True);
        });
    }

    [Test]
    public void RegressionJsonFormatter_EmitsCamelCaseEnumStrings()
    {
        RegressionFixtureResult fixtureResult = new(
            "Demonstrators/hw.blade",
            RegressionFixtureOutcome.HwFailed,
            "failed",
            ["detail"],
            artifactDirectoryPath: "/repo/.artifacts/regressions/run/fail",
            hardwareAttempted: true);
        RegressionRunResult result = new("/repo", [fixtureResult]);

        using JsonDocument document = JsonDocument.Parse(RegressionJsonFormatter.Format(result));
        JsonElement fixture = document.RootElement.GetProperty("fixtureResults")[0];

        Assert.Multiple(() =>
        {
            Assert.That(document.RootElement.GetProperty("succeeded").GetBoolean(), Is.True);
            Assert.That(fixture.GetProperty("outcome").GetString(), Is.EqualTo("hwFailed"));
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

        string message = (string)formatter.Invoke(null, [0x0000012Cu, 0x000000C8u])!;

        Assert.That(
            message,
            Is.EqualTo(
                """
                hardware output mismatch:
                            hex        | unsigned   |      signed
                  expected  0x0000012C |        300 |         300
                  actual    0x000000C8 |        200 |         200
                """));
    }

    [Test]
    public void PassHwFixture_RunsDirective_ParsesMixedLiterals()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        WriteHardwareRuntime(temp);
        temp.WriteFile("Demonstrators/hw_mixed_runs.blade", """
        // EXPECT: pass-hw
        // RUNS:
        // - [] = 1234
        // - [ 0 ] = 1234
        // - [ 0, -10, 0x12345 ] = -1
        extern cog var rt_result: u32;
        cog task main {
            rt_result = 1234;
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
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Pass));
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
            Assert.That(fixtureResult.Details, Has.Some.Contains("Invalid RUNS entry '0, 1 = 2'. Expected '[ ... ] = value'."));
        });
    }

    [Test]
    public void XFailHwFixture_WithoutHardwarePort_IsXFail()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        WriteHardwareRuntime(temp);
        temp.WriteFile("Demonstrators/xfailhw_no_port.blade", """
        // EXPECT: xfail-hw
        // RUNS:
        // - [] = 0x1
        extern cog var rt_result: u32;
        cog task main {
            rt_result = 1;
        }
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
            HardwarePort = "",  // disable hardware; xfail-hw without a run attempt is XFail
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(r =>
            r.RelativePath == "Demonstrators/xfailhw_no_port.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.XFail));
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
    public void XFailFixture_WithMatchingDiagnostics_IsUnexpectedPass()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.MakeDir("Demonstrators/Binder");
        temp.WriteFile("Demonstrators/Binder/fail_control_flow_contexts.blade", """
        // EXPECT: xfail
        // DIAGNOSTICS: E0202
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
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.UnexpectedPass));
            Assert.That(fixtureResult.Summary, Is.EqualTo("unexpected pass"));
            Assert.That(fixtureResult.Details, Is.Empty);
        });
    }

    [Test]
    public void XFailFixture_WhenExpectedDiagnosticsDisappear_IsUnexpectedPass()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/xfail_resolved.blade", """
        // EXPECT: xfail
        // DIAGNOSTICS: E0202
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
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.UnexpectedPass));
            Assert.That(fixtureResult.Summary, Is.EqualTo("unexpected pass"));
            Assert.That(fixtureResult.Details, Has.Some.Contains("missing diagnostic code E0202: expected at least 1, got 0"));
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
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Pass));
            Assert.That(fixtureResult.Summary, Is.EqualTo("passed"));
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
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Pass));
            Assert.That(fixtureResult.Summary, Is.EqualTo("passed"));
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
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Pass));
            Assert.That(fixtureResult.Summary, Is.EqualTo("passed"));
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

    private static void WriteHardwareRuntime(TempDirectory temp)
    {
        temp.WriteFile("Blade.HwTestRunner/Runtime.spin2", """
        CON
            ' <<BLADE_CON>>
        DAT
            JMP #rt_start
        rt_param0 LONG 0
        rt_param1 LONG 0
        rt_param2 LONG 0
        rt_param3 LONG 0
        rt_param4 LONG 0
        rt_param5 LONG 0
        rt_param6 LONG 0
        rt_param7 LONG 0
        rt_start
            JMP #blade_entry
        blade_halt
            REP #1, #0
            NOP
        rt_result LONG 0
        ' <<BLADE_DAT>>
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
        bool hasHardwareRuntime = File.Exists(Path.Combine(temp.Path, "Blade.HwTestRunner", "Runtime.spin2"));
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
            properties.Add("    \"hardwareRuntimePath\": \"Blade.HwTestRunner/Runtime.spin2\"");
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
}
