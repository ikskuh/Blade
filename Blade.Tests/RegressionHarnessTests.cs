using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
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
    public void PassHwFixture_WithoutConfiguredPort_UsesImplicitHardwareRuntimeAndPasses()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        WriteHardwareRuntime(temp);
        temp.WriteFile("Demonstrators/hw_runtime_injected.blade", """
        // EXPECT: pass-hw
        // OUTPUT: 0x0
        // STAGE: final-asm
        // CONTAINS:
        // - rt_result LONG 0
        extern reg var rt_result: u32;
        rt_result = 0;
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
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
    public void PassHwFixture_WithExplicitRuntime_KeepsExplicitRuntimeInsteadOfImplicitHardwareRuntime()
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
        // OUTPUT: 0x0
        // ARGS: --runtime=custom_runtime.spin2
        // STAGE: final-asm
        // CONTAINS:
        // - custom_marker LONG 0
        // ! rt_result LONG 0
        var x: u32 = 1;
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
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
    public void PassHwFixture_RequiresOutputDirective()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/missing_output.blade", """
        // EXPECT: pass-hw
        var x: u32 = 0;
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(result =>
            result.RelativePath == "Demonstrators/missing_output.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Fail));
            Assert.That(fixtureResult.Details, Has.Some.Contains("EXPECT: pass-hw requires OUTPUT."));
        });
    }

    [Test]
    public void OutputDirective_IsRejectedForPlainPassFixture()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("Demonstrators/output_on_plain_pass.blade", """
        // EXPECT: pass
        // OUTPUT: 0x0
        var x: u32 = 0;
        """);

        RegressionRunResult result = RegressionRunner.Run(new RegressionRunOptions
        {
            RepositoryRootPath = temp.Path,
            WriteFailureArtifacts = false,
        });

        RegressionFixtureResult fixtureResult = result.FixtureResults.Single(result =>
            result.RelativePath == "Demonstrators/output_on_plain_pass.blade");
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Fail));
            Assert.That(fixtureResult.Details, Has.Some.Contains("OUTPUT is only valid with EXPECT: pass-hw."));
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
    public void PassHwFixture_WithConfiguredPort_AttemptsHardwareExecutionAndSurfacesFailures()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        WriteHardwareRuntime(temp);
        temp.WriteFile("Demonstrators/hw_exec.blade", """
        // EXPECT: pass-hw
        // OUTPUT: 0x0
        extern reg var rt_result: u32;
        rt_result = 0;
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
            Assert.That(fixtureResult.Outcome, Is.EqualTo(RegressionFixtureOutcome.Fail));
            Assert.That(fixtureResult.Details, Has.Some.StartsWith("hardware execution failed:"));
        });
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

    [Test]
    public void FullRegressionSuite_WithIrGuard_MovesObservedTypesToCovered()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("RegressionTests/ir-regression-guard.json", """
        {
            "bound": {
                "covered": [],
                "uncovered": ["BoundProgram"]
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
        Assert.That(ReadGuardArray(temp, "bound", "covered"), Does.Contain("BoundProgram"));
        Assert.That(ReadGuardArray(temp, "bound", "uncovered"), Does.Not.Contain("BoundProgram"));
    }

    [Test]
    public void FullRegressionSuite_WithIrGuard_ReportsCoverageRegressions()
    {
        using TempDirectory temp = new();
        WriteMinimalRegressionRepository(temp);
        temp.WriteFile("RegressionTests/ir-regression-guard.json", """
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
        temp.WriteFile("RegressionTests/ir-regression-guard.json", """
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
        temp.MakeDir("Blade");
        temp.WriteFile("justfile", "fuzz:\n    false\n");
        temp.WriteFile("Examples/smoke.blade", "fn inc(x: u32) -> u32 { return x + 1; }");
    }

    private static void WriteHardwareRuntime(TempDirectory temp)
    {
        temp.WriteFile("Blade.HwTestRunner/Runtime.spin2", """
        CON
            ' <<BLADE_CON>>
        DAT
            JMP #blade_entry
        blade_halt
            REP #1, #0
            NOP
        rt_result LONG 0
        ' <<BLADE_DAT>>
        """);
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
