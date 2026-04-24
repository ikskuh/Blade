using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Blade;
using Blade.Diagnostics;
using Blade.HwTestRunner;
using Blade.IR;
using Blade.Source;

namespace Blade.Regressions;

public sealed class RegressionRunOptions
{
    public string? RepositoryRootPath { get; init; }
    public string? ConfigPath { get; init; }
    public IReadOnlyList<string> Filters { get; init; } = [];
    public bool WriteFailureArtifacts { get; init; } = true;
    public string? HardwarePort { get; init; }
    public HardwareLoaderKind? HardwareLoader { get; init; }
    public bool? HardwareTurbopropNoVersionCheck { get; init; }
    public bool Json { get; init; }
}

public sealed class RegressionRunResult
{
    public RegressionRunResult(
        string repositoryRootPath,
        IReadOnlyList<RegressionFixtureResult> fixtureResults,
        RegressionIrCoverageReport? irCoverageReport = null)
    {
        RepositoryRootPath = repositoryRootPath;
        FixtureResults = fixtureResults;
        IrCoverageReport = irCoverageReport;
    }

    public string RepositoryRootPath { get; }
    public IReadOnlyList<RegressionFixtureResult> FixtureResults { get; }
    public RegressionIrCoverageReport? IrCoverageReport { get; }
    public int PassCount => FixtureResults.Count(result => result.Outcome == RegressionFixtureOutcome.Pass);
    public int FailCount => FixtureResults.Count(result => result.Outcome == RegressionFixtureOutcome.Fail);
    public int XFailCount => FixtureResults.Count(result => result.Outcome == RegressionFixtureOutcome.XFail);
    public int UnexpectedPassCount => FixtureResults.Count(result => result.Outcome == RegressionFixtureOutcome.UnexpectedPass);
    public int SkipCount => FixtureResults.Count(result => result.Outcome == RegressionFixtureOutcome.Skipped);
    public int HwFailedCount => FixtureResults.Count(result => result.Outcome == RegressionFixtureOutcome.HwFailed);
    public bool Succeeded => FailCount == 0
        && UnexpectedPassCount == 0
        && !(IrCoverageReport?.HasRegressions ?? false);
}

public sealed class RegressionFixtureResult
{
    public RegressionFixtureResult(
        string relativePath,
        RegressionFixtureOutcome outcome,
        string summary,
        IReadOnlyList<string> details,
        string? artifactDirectoryPath,
        bool hardwareAttempted = false)
    {
        RelativePath = relativePath;
        Outcome = outcome;
        Summary = summary;
        Details = details;
        ArtifactDirectoryPath = artifactDirectoryPath;
        HardwareAttempted = hardwareAttempted;
    }

    public string RelativePath { get; }
    public RegressionFixtureOutcome Outcome { get; }
    public string Summary { get; }
    public IReadOnlyList<string> Details { get; }
    public string? ArtifactDirectoryPath { get; }
    public bool HardwareAttempted { get; }
}

public enum RegressionFixtureOutcome
{
    Pass,
    Fail,
    XFail,
    UnexpectedPass,
    Skipped,
    HwFailed,
}

public enum RegressionFixtureKind
{
    Blade,
    BladeCrash,
}

public enum RegressionExpectationKind
{
    Pass,
    PassHw,
    Fail,
    XFail,
    XFailHw,
}

public enum RegressionStage
{
    Bound,
    MirPreOptimization,
    Mir,
    LirPreOptimization,
    Lir,
    AsmirPreOptimization,
    Asmir,
    FinalAsm,
}

public enum FlexspinExpectation
{
    Auto,
    Required,
    Forbidden,
}

public sealed class RegressionFixture
{
    public RegressionFixture(
        string absolutePath,
        string relativePath,
        RegressionFixtureKind kind,
        string text,
        string bodyText,
        RegressionExpectation expectation)
    {
        AbsolutePath = absolutePath;
        RelativePath = relativePath;
        Kind = kind;
        Text = text;
        BodyText = bodyText;
        Expectation = expectation;
    }

    public string AbsolutePath { get; }
    public string RelativePath { get; }
    public RegressionFixtureKind Kind { get; }
    public string Text { get; }
    public string BodyText { get; }
    public RegressionExpectation Expectation { get; }
}

internal enum RegressionPoolExpectation
{
    Accept,
    Reject,
    Encoded,
}

internal sealed class RegressionPoolConfiguration
{
    public RegressionPoolConfiguration(string absolutePath, string relativePath, RegressionPoolExpectation expectation)
    {
        AbsolutePath = absolutePath;
        RelativePath = relativePath;
        Expectation = expectation;
    }

    public string AbsolutePath { get; }
    public string RelativePath { get; }
    public RegressionPoolExpectation Expectation { get; }
}

internal sealed class DiscoveredRegressionFixture
{
    public DiscoveredRegressionFixture(string absolutePath, string relativePath, RegressionPoolExpectation poolExpectation)
    {
        AbsolutePath = absolutePath;
        RelativePath = relativePath;
        PoolExpectation = poolExpectation;
    }

    public string AbsolutePath { get; }
    public string RelativePath { get; }
    public RegressionPoolExpectation PoolExpectation { get; }
}

internal sealed class RegressionSuiteConfiguration
{
    public RegressionSuiteConfiguration(
        string repositoryRootPath,
        string configPath,
        IReadOnlyList<RegressionPoolConfiguration> pools,
        string? hardwareRuntimePath,
        string? irCoverageGuardPath)
    {
        RepositoryRootPath = repositoryRootPath;
        ConfigPath = configPath;
        Pools = pools;
        HardwareRuntimePath = hardwareRuntimePath;
        IrCoverageGuardPath = irCoverageGuardPath;
    }

    public string RepositoryRootPath { get; }
    public string ConfigPath { get; }
    public IReadOnlyList<RegressionPoolConfiguration> Pools { get; }
    public string? HardwareRuntimePath { get; }
    public string? IrCoverageGuardPath { get; }
}

public enum SnippetKind
{
    Positive,
    Negative,
    Count,
}

public sealed class SnippetItem
{
    private SnippetItem(SnippetKind kind, string text, int count)
    {
        Kind = kind;
        Text = text;
        Count = count;
    }

    public SnippetKind Kind { get; }
    public string Text { get; }
    public int Count { get; }

    public static SnippetItem Positive(string text) => new(SnippetKind.Positive, text, 0);
    public static SnippetItem Negative(string text) => new(SnippetKind.Negative, text, 0);

    public static SnippetItem ExactCount(string text, int count)
    {
        if (count == 0)
            return Negative(text);

        return new SnippetItem(SnippetKind.Count, text, count);
    }
}

public sealed class HardwareRunExpectation
{
    public HardwareRunExpectation(
        IReadOnlyList<FixtureParameter> parameters,
        IReadOnlyList<string> parameterLiterals,
        uint expectedOutput)
    {
        Parameters = parameters;
        ParameterLiterals = parameterLiterals;
        ExpectedOutput = expectedOutput;
    }

    public IReadOnlyList<FixtureParameter> Parameters { get; }
    public IReadOnlyList<string> ParameterLiterals { get; }
    public uint ExpectedOutput { get; }
}

public sealed class RegressionExpectation
{
    public RegressionExpectation(
        RegressionExpectationKind expectationKind,
        RegressionStage? stage,
        IReadOnlyList<SnippetItem> containsSnippets,
        IReadOnlyList<SnippetItem> sequenceSnippets,
        string? exactText,
        IReadOnlyList<string> looseDiagnosticCodes,
        IReadOnlyList<ExpectedDiagnostic> exactDiagnostics,
        FlexspinExpectation flexspinExpectation,
        IReadOnlyList<string> compilerArgs,
        IReadOnlyList<HardwareRunExpectation> hardwareRuns)
    {
        ExpectationKind = expectationKind;
        Stage = stage;
        ContainsSnippets = containsSnippets;
        SequenceSnippets = sequenceSnippets;
        ExactText = exactText;
        LooseDiagnosticCodes = looseDiagnosticCodes;
        ExactDiagnostics = exactDiagnostics;
        FlexspinExpectation = flexspinExpectation;
        CompilerArgs = compilerArgs;
        HardwareRuns = hardwareRuns;
    }

    public RegressionExpectationKind ExpectationKind { get; }
    public RegressionStage? Stage { get; }
    public IReadOnlyList<SnippetItem> ContainsSnippets { get; }
    public IReadOnlyList<SnippetItem> SequenceSnippets { get; }
    public string? ExactText { get; }
    public IReadOnlyList<string> LooseDiagnosticCodes { get; }
    public IReadOnlyList<ExpectedDiagnostic> ExactDiagnostics { get; }
    public FlexspinExpectation FlexspinExpectation { get; }
    public IReadOnlyList<string> CompilerArgs { get; }
    public IReadOnlyList<HardwareRunExpectation> HardwareRuns { get; }
    public bool HasCodeAssertions => ContainsSnippets.Count > 0 || SequenceSnippets.Count > 0 || ExactText is not null;
    public bool HasDiagnosticAssertions => LooseDiagnosticCodes.Count > 0 || ExactDiagnostics.Count > 0;
}

public sealed class ExpectedDiagnostic
{
    public ExpectedDiagnostic(string code, int? line, string? message)
    {
        Code = code;
        Line = line;
        Message = message;
    }

    public string Code { get; }
    public int? Line { get; }
    public string? Message { get; }

    public string Display()
    {
        List<string> parts = [];
        if (Line is not null)
            parts.Add($"L{Line.Value}");
        parts.Add(Code);
        string joined = string.Join(", ", parts);
        if (Message is null)
            return joined;
        return $"{joined}: {Message}";
    }
}

public sealed class ActualDiagnostic
{
    public ActualDiagnostic(string code, int line, string message)
    {
        Code = code;
        Line = line;
        Message = message;
    }

    public string Code { get; }
    public int Line { get; }
    public string Message { get; }

    public string Display() => $"L{Line}, {Code}: {Message}";
}

public static class RegressionRunner
{
    public static RegressionRunResult Run(RegressionRunOptions? options = null)
    {
        RegressionRunOptions effectiveOptions = options ?? new RegressionRunOptions();
        RegressionSuiteConfiguration configuration = RegressionConfigurationLoader.Load(effectiveOptions);
        string repositoryRootPath = configuration.RepositoryRootPath;
        string? hardwarePort = HardwarePortResolver.Resolve(effectiveOptions.HardwarePort);
        HardwareLoaderKind hardwareLoader = HardwareLoaderSettings.ResolveLoader(effectiveOptions.HardwareLoader);
        bool hardwareTurbopropNoVersionCheck = HardwareLoaderSettings.ResolveTurbopropNoVersionCheck(effectiveOptions.HardwareTurbopropNoVersionCheck);
        bool isFullRun = effectiveOptions.Filters.Count == 0;

        FlexspinProbeResult flexspinProbe = FlexspinRunner.ProbeAvailability();
        List<DiscoveredRegressionFixture> fixtures = DiscoverFixtures(configuration, effectiveOptions.Filters);
        List<RegressionFixtureResult> fixtureResults = [];
        ArtifactWriter artifactWriter = new(repositoryRootPath, effectiveOptions.WriteFailureArtifacts);
        RegressionIrCoverageSession? irCoverageSession = RegressionIrCoverageSession.TryCreate(configuration.IrCoverageGuardPath, isFullRun);

        foreach (DiscoveredRegressionFixture fixture in fixtures)
        {
            RegressionFixtureResult result = EvaluateFixture(
                configuration,
                fixture,
                artifactWriter,
                flexspinProbe,
                irCoverageSession,
                hardwarePort,
                hardwareLoader,
                hardwareTurbopropNoVersionCheck);
            fixtureResults.Add(result);
        }

        RegressionIrCoverageReport? irCoverageReport = irCoverageSession?.Complete();
        return new RegressionRunResult(repositoryRootPath, fixtureResults, irCoverageReport);
    }

    private static List<DiscoveredRegressionFixture> DiscoverFixtures(
        RegressionSuiteConfiguration configuration,
        IReadOnlyList<string> filters)
    {
        Dictionary<string, DiscoveredRegressionFixture> fixturesByPath = new(PathComparer.Instance);
        foreach (RegressionPoolConfiguration pool in configuration.Pools)
        {
            AddFixturePaths(fixturesByPath, configuration.RepositoryRootPath, pool, "*.blade");
            AddFixturePaths(fixturesByPath, configuration.RepositoryRootPath, pool, "*.blade.crash");
        }

        IEnumerable<DiscoveredRegressionFixture> filteredPaths = fixturesByPath.Values;
        if (filters.Count > 0)
        {
            filteredPaths = filteredPaths.Where(path =>
                filters.Any(filter => path.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        }

        return filteredPaths
            .OrderBy(path => path.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddFixturePaths(
        Dictionary<string, DiscoveredRegressionFixture> fixturesByPath,
        string repositoryRootPath,
        RegressionPoolConfiguration pool,
        string searchPattern)
    {
        string[] discovered = Directory.GetFiles(pool.AbsolutePath, searchPattern, SearchOption.AllDirectories);
        foreach (string fixturePath in discovered)
        {
            string absolutePath = Path.GetFullPath(fixturePath);
            string relativePath = Path.GetRelativePath(repositoryRootPath, absolutePath).Replace('\\', '/');
            DiscoveredRegressionFixture fixture = new(absolutePath, relativePath, pool.Expectation);
            if (!fixturesByPath.TryAdd(absolutePath, fixture))
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"Fixture '{relativePath}' was discovered more than once. Check for overlapping regression pools."));
            }
        }
    }

    private static RegressionFixtureResult EvaluateFixture(
        RegressionSuiteConfiguration configuration,
        DiscoveredRegressionFixture discoveredFixture,
        ArtifactWriter artifactWriter,
        FlexspinProbeResult flexspinProbe,
        RegressionIrCoverageSession? irCoverageSession,
        string? hardwarePort,
        HardwareLoaderKind hardwareLoader,
        bool hardwareTurbopropNoVersionCheck)
    {
        string repositoryRootPath = configuration.RepositoryRootPath;
        string fixturePath = discoveredFixture.AbsolutePath;
        string relativePath = discoveredFixture.RelativePath;
        RegressionFixture? fixture = null;

        try
        {
            fixture = RegressionFixtureParser.Parse(discoveredFixture);
            if (fixture.Kind == RegressionFixtureKind.BladeCrash)
            {
                _ = ExecuteBladeCrashFixture(fixture);
                return new RegressionFixtureResult(relativePath, RegressionFixtureOutcome.Pass, "passed", [], null);
            }

            EvaluatedFixture evaluatedFixture = ExecuteFixture(configuration, fixture, irCoverageSession);
            List<string> issues = [];
            bool hardwareAttempted = false;

            issues.AddRange(EvaluateDiagnostics(fixture.Expectation, evaluatedFixture.Diagnostics));
            issues.AddRange(EvaluateCodeAssertions(fixture, evaluatedFixture));
            if (ShouldRunFlexspin(fixture) && !flexspinProbe.IsAvailable)
            {
                List<string> details =
                [
                    "skipped: flexspin is not available",
                    $"flexspin probe: {flexspinProbe.ProbeSummary}",
                ];
                return new RegressionFixtureResult(relativePath, RegressionFixtureOutcome.Skipped, "skipped", details, null);
            }

            issues.AddRange(EvaluateFlexspin(fixture, evaluatedFixture));
            if (issues.Count == 0)
            {
                HardwareExecutionResult hardwareExecution = EvaluateHardwareExecution(
                    fixture,
                    evaluatedFixture,
                    hardwarePort,
                    hardwareLoader,
                    hardwareTurbopropNoVersionCheck);
                hardwareAttempted = hardwareExecution.Attempted;
                if (hardwareExecution.BinaryBytes is not null)
                    evaluatedFixture = evaluatedFixture.WithHardwareBinary(hardwareExecution.BinaryBytes);
                if (hardwareExecution.IsHardwareFailed)
                {
                    string hwFailedSummary = hardwareExecution.Issues.Count > 0 ? hardwareExecution.Issues[0] : "hardware runner failed";
                    string? hwArtifactDirectoryPath = artifactWriter.WriteFailureArtifacts(fixture, evaluatedFixture, hwFailedSummary, hardwareExecution.Issues);
                    return new RegressionFixtureResult(
                        relativePath,
                        RegressionFixtureOutcome.HwFailed,
                        hwFailedSummary,
                        hardwareExecution.Issues,
                        hwArtifactDirectoryPath,
                        hardwareExecution.Attempted);
                }
                issues.AddRange(hardwareExecution.Issues);
            }

            RegressionFixtureOutcome outcome = ComputeOutcome(
                fixture.Expectation.ExpectationKind,
                issues.Count == 0,
                fixture,
                evaluatedFixture);
            string summary = BuildSummary(fixture.Expectation, evaluatedFixture, outcome, issues);
            string? artifactDirectoryPath = null;
            if (issues.Count > 0 || outcome == RegressionFixtureOutcome.UnexpectedPass)
            {
                artifactDirectoryPath = artifactWriter.WriteFailureArtifacts(fixture, evaluatedFixture, summary, issues);
            }

            return new RegressionFixtureResult(
                relativePath,
                outcome,
                summary,
                issues,
                artifactDirectoryPath,
                hardwareAttempted);
        }
        catch (Exception ex)
        {
            bool includeExceptionStackTrace = IsBladeCrashFixturePath(fixturePath);
            List<string> details = BuildUnhandledFixtureDetails(ex, includeExceptionStackTrace);
            EvaluatedFixture failedFixture = EvaluatedFixture.Empty(relativePath);
            RegressionFixture syntheticFixture = fixture ?? new(
                fixturePath,
                relativePath,
                RegressionFixtureKind.Blade,
                string.Empty,
                string.Empty,
                new RegressionExpectation(
                    RegressionExpectationKind.Fail,
                    null,
                    [],
                    [],
                    null,
                    [],
                    [],
                    FlexspinExpectation.Forbidden,
                    [],
                    []));
            string summary = "fixture evaluation crashed";
            string? artifactDirectoryPath = artifactWriter.WriteFailureArtifacts(syntheticFixture, failedFixture, summary, details);
            return new RegressionFixtureResult(relativePath, RegressionFixtureOutcome.Fail, summary, details, artifactDirectoryPath);
        }
    }

    private static EvaluatedFixture ExecuteFixture(
        RegressionSuiteConfiguration configuration,
        RegressionFixture fixture,
        RegressionIrCoverageSession? irCoverageSession)
    {
        return fixture.Kind switch
        {
            RegressionFixtureKind.Blade => ExecuteBladeFixture(configuration, fixture, irCoverageSession),
            RegressionFixtureKind.BladeCrash => ExecuteBladeCrashFixture(fixture),
            _ => throw new InvalidOperationException($"Unknown fixture kind '{fixture.Kind}'."),
        };
    }

    private static EvaluatedFixture ExecuteBladeFixture(
        RegressionSuiteConfiguration configuration,
        RegressionFixture fixture,
        RegressionIrCoverageSession? irCoverageSession)
    {
        CompilationOptions options = BuildCompilationOptions(configuration.HardwareRuntimePath, fixture.Expectation, fixture.AbsolutePath);
        CompilationResult compilation = CompilerDriver.Compile(fixture.Text, fixture.AbsolutePath, options);
        List<ActualDiagnostic> diagnostics = compilation.Diagnostics
            .Select(diag =>
            {
                SourceLocation location = diag.GetLocation();
                return new ActualDiagnostic(diag.FormatCode(), location.Line, diag.Message);
            })
            .ToList();

        Dictionary<RegressionStage, string> stageOutputs = [];
        if (compilation.IrBuildResult is not null)
        {
            irCoverageSession?.Record(compilation.IrBuildResult);
            Dictionary<string, string> dumps = DumpContentBuilder.Build(
                new DumpSelection
                {
                    DumpBound = true,
                    DumpMirPreOptimization = true,
                    DumpMir = true,
                    DumpLirPreOptimization = true,
                    DumpLir = true,
                    DumpAsmirPreOptimization = true,
                    DumpAsmir = true,
                    DumpFinalAsm = true,
                },
                compilation.IrBuildResult);
            stageOutputs[RegressionStage.Bound] = dumps["00_bound.ir"];
            stageOutputs[RegressionStage.MirPreOptimization] = dumps["05_mir_preopt.ir"];
            stageOutputs[RegressionStage.Mir] = dumps["10_mir.ir"];
            stageOutputs[RegressionStage.LirPreOptimization] = dumps["15_lir_preopt.ir"];
            stageOutputs[RegressionStage.Lir] = dumps["20_lir.ir"];
            stageOutputs[RegressionStage.AsmirPreOptimization] = dumps["25_asmir_preopt.ir"];
            stageOutputs[RegressionStage.Asmir] = dumps["30_asmir.ir"];
            stageOutputs[RegressionStage.FinalAsm] = dumps["40_final.spin2"];
        }

        return new EvaluatedFixture(
            diagnostics,
            stageOutputs,
            compilation.IrBuildResult?.AssemblyText,
            fixture.BodyText,
            null);
    }

    private static EvaluatedFixture ExecuteBladeCrashFixture(RegressionFixture fixture)
    {
        _ = CompilerDriver.CompileFile(fixture.AbsolutePath);
        return EvaluatedFixture.Empty(fixture.RelativePath);
    }

    private static bool IsBladeCrashFixturePath(string fixturePath)
    {
        return fixturePath.EndsWith(".blade.crash", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> BuildUnhandledFixtureDetails(Exception ex, bool includeExceptionStackTrace)
    {
        List<string> details =
        [
            $"Unhandled regression runner error: {ex.Message}",
        ];

        if (!includeExceptionStackTrace)
            return details;

        details.Add("Exception stack trace:");
        details.AddRange(SplitLines(ex.ToString()));
        return details;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        using StringReader reader = new(text);
        while (reader.ReadLine() is string line)
            yield return line;
    }

    private static CompilationOptions BuildCompilationOptions(
        string? defaultHardwareRuntimePath,
        RegressionExpectation expectation,
        string fixturePath)
    {
        List<string> effectiveArgs = new(expectation.CompilerArgs);
        if ((expectation.ExpectationKind == RegressionExpectationKind.PassHw
                || expectation.ExpectationKind == RegressionExpectationKind.XFailHw)
            && !effectiveArgs.Any(static arg => arg.StartsWith("--runtime=", StringComparison.Ordinal)))
        {
            if (string.IsNullOrWhiteSpace(defaultHardwareRuntimePath))
            {
                throw new InvalidOperationException(
                    "Hardware fixtures require --runtime=... in ARGS or a configured hardwareRuntimePath.");
            }

            effectiveArgs.Add($"--runtime={defaultHardwareRuntimePath}");
        }

        string baseDirectory = Path.GetDirectoryName(fixturePath) ?? Environment.CurrentDirectory;
        return CompilationOptionsCommandLine.Parse(effectiveArgs, baseDirectory);
    }

    private static List<string> EvaluateDiagnostics(RegressionExpectation expectation, IReadOnlyList<ActualDiagnostic> diagnostics)
    {
        List<string> issues = [];

        if (expectation.ExactDiagnostics.Count > 0)
        {
            List<ActualDiagnostic> remaining = diagnostics.ToList();
            foreach (ExpectedDiagnostic expected in expectation.ExactDiagnostics)
            {
                ActualDiagnostic? match = remaining.FirstOrDefault(actual => MatchesExpectedDiagnostic(actual, expected));
                if (match is null)
                {
                    issues.Add($"missing diagnostic: {expected.Display()}");
                    continue;
                }

                remaining.Remove(match);
            }

            foreach (ActualDiagnostic extra in remaining)
                issues.Add($"unexpected diagnostic: {extra.Display()}");

            return issues;
        }

        if (expectation.LooseDiagnosticCodes.Count > 0)
        {
            foreach (IGrouping<string, string> group in expectation.LooseDiagnosticCodes.GroupBy(code => code, StringComparer.Ordinal))
            {
                int actualCount = diagnostics.Count(diag => diag.Code.Equals(group.Key, StringComparison.Ordinal));
                if (actualCount < group.Count())
                {
                    issues.Add($"missing diagnostic code {group.Key}: expected at least {group.Count()}, got {actualCount}");
                }
            }

            return issues;
        }

        if ((expectation.ExpectationKind == RegressionExpectationKind.Pass
                || expectation.ExpectationKind == RegressionExpectationKind.PassHw
                || expectation.ExpectationKind == RegressionExpectationKind.XFailHw)
            && diagnostics.Count > 0)
        {
            foreach (ActualDiagnostic diagnostic in diagnostics)
                issues.Add($"unexpected diagnostic: {diagnostic.Display()}");
        }

        if (expectation.ExpectationKind == RegressionExpectationKind.Fail && !diagnostics.Any(diag => diag.Code.StartsWith('E')))
            issues.Add("expected at least one error diagnostic, but compilation was clean");

        return issues;
    }

    private static bool MatchesExpectedDiagnostic(ActualDiagnostic actual, ExpectedDiagnostic expected)
    {
        if (!actual.Code.Equals(expected.Code, StringComparison.Ordinal))
            return false;
        if (expected.Line is not null && actual.Line != expected.Line.Value)
            return false;
        if (expected.Message is not null && !actual.Message.Equals(expected.Message, StringComparison.Ordinal))
            return false;
        return true;
    }

    private static List<string> EvaluateCodeAssertions(RegressionFixture fixture, EvaluatedFixture evaluatedFixture)
    {
        List<string> issues = [];
        RegressionExpectation expectation = fixture.Expectation;
        if (!expectation.HasCodeAssertions)
            return issues;

        if (fixture.Kind != RegressionFixtureKind.Blade)
        {
            issues.Add("only .blade fixtures support code assertions");
            return issues;
        }

        if (expectation.Stage is null)
        {
            issues.Add("fixture has code assertions but no STAGE");
            return issues;
        }

        if (!evaluatedFixture.StageOutputs.TryGetValue(expectation.Stage.Value, out string? actualText))
        {
            issues.Add($"requested stage '{StageName(expectation.Stage.Value)}' is unavailable");
            return issues;
        }

        string normalizedActual = CodeNormalizer.NormalizeBladeStage(expectation.Stage.Value, actualText);
        issues.AddRange(EvaluateNormalizedAssertions(expectation, normalizedActual, expectation.Stage.Value));
        return issues;
    }

    private static bool WildcardsEnabled(RegressionStage? stage)
    {
        return stage is null
            or RegressionStage.AsmirPreOptimization
            or RegressionStage.Asmir
            or RegressionStage.FinalAsm;
    }

    private static List<string> EvaluateNormalizedAssertions(
        RegressionExpectation expectation,
        string normalizedActual,
        RegressionStage? stage)
    {
        List<string> issues = [];
        bool wildcards = WildcardsEnabled(stage);

        Dictionary<int, string> containsBindings = new();
        foreach (SnippetItem item in expectation.ContainsSnippets)
        {
            string normalizedSnippet = NormalizeExpectedCode(item.Text, stage);

            switch (item.Kind)
            {
                case SnippetKind.Positive:
                    if (!SnippetMatcher.Contains(normalizedActual, normalizedSnippet, containsBindings, wildcards))
                        issues.Add($"missing snippet: {item.Text}");
                    break;

                case SnippetKind.Negative:
                    if (SnippetMatcher.Contains(normalizedActual, normalizedSnippet, containsBindings, wildcards))
                        issues.Add($"unexpected snippet present: {item.Text}");
                    break;

                case SnippetKind.Count:
                    int actualCount = SnippetMatcher.CountOccurrences(normalizedActual, normalizedSnippet, containsBindings, wildcards);
                    if (actualCount != item.Count)
                        issues.Add($"expected {item.Count} occurrence(s) of snippet, found {actualCount}: {item.Text}");
                    break;
            }
        }

        if (expectation.SequenceSnippets.Count > 0)
            EvaluateSequenceAssertions(expectation.SequenceSnippets, normalizedActual, stage, wildcards, issues);

        if (expectation.ExactText is not null)
        {
            string normalizedExpected = NormalizeExpectedCode(expectation.ExactText, stage);
            if (!string.Equals(normalizedActual, normalizedExpected, StringComparison.Ordinal))
                issues.Add($"exact text mismatch: {NormalizedDiffBuilder.Build(normalizedExpected, normalizedActual)}");
        }

        return issues;
    }

    private static void EvaluateSequenceAssertions(
        IReadOnlyList<SnippetItem> sequenceSnippets,
        string normalizedActual,
        RegressionStage? stage,
        bool wildcardsEnabled,
        List<string> issues)
    {
        Dictionary<int, string> sequenceBindings = new();
        int index = 0;
        int previousPositiveEnd = 0;
        List<SnippetItem> pendingNegatives = [];

        foreach (SnippetItem item in sequenceSnippets)
        {
            string normalizedSnippet = NormalizeExpectedCode(item.Text, stage);

            switch (item.Kind)
            {
                case SnippetKind.Negative:
                    pendingNegatives.Add(item);
                    break;

                case SnippetKind.Positive:
                {
                    int foundIndex = SnippetMatcher.IndexOf(normalizedActual, normalizedSnippet, index, sequenceBindings, wildcardsEnabled, out int matchLength);
                    if (foundIndex < 0)
                    {
                        issues.Add($"missing ordered snippet: {item.Text}");
                        return;
                    }

                    CheckPendingNegatives(normalizedActual, previousPositiveEnd, foundIndex, pendingNegatives, stage, sequenceBindings, wildcardsEnabled, issues);
                    pendingNegatives.Clear();
                    previousPositiveEnd = foundIndex + matchLength;
                    index = previousPositiveEnd;
                    break;
                }

                case SnippetKind.Count:
                {
                    int countIndex = index;
                    for (int i = 0; i < item.Count; i++)
                    {
                        int foundIndex = SnippetMatcher.IndexOf(normalizedActual, normalizedSnippet, countIndex, sequenceBindings, wildcardsEnabled, out int matchLength);
                        if (foundIndex < 0)
                        {
                            issues.Add($"expected {item.Count} consecutive occurrence(s) of ordered snippet, found {i}: {item.Text}");
                            return;
                        }

                        if (i == 0)
                        {
                            CheckPendingNegatives(normalizedActual, previousPositiveEnd, foundIndex, pendingNegatives, stage, sequenceBindings, wildcardsEnabled, issues);
                            pendingNegatives.Clear();
                        }

                        countIndex = foundIndex + matchLength;
                    }

                    previousPositiveEnd = countIndex;
                    index = countIndex;
                    break;
                }
            }
        }

        // Check trailing negatives against rest of the text.
        if (pendingNegatives.Count > 0)
            CheckPendingNegatives(normalizedActual, previousPositiveEnd, normalizedActual.Length, pendingNegatives, stage, sequenceBindings, wildcardsEnabled, issues);
    }

    private static void CheckPendingNegatives(
        string normalizedActual,
        int gapStart,
        int gapEnd,
        List<SnippetItem> pendingNegatives,
        RegressionStage? stage,
        Dictionary<int, string> bindings,
        bool wildcardsEnabled,
        List<string> issues)
    {
        if (gapStart >= gapEnd)
            return;

        string gap = normalizedActual[gapStart..gapEnd];
        foreach (SnippetItem negative in pendingNegatives)
        {
            string normalizedNeg = NormalizeExpectedCode(negative.Text, stage);
            if (SnippetMatcher.Contains(gap, normalizedNeg, bindings, wildcardsEnabled))
                issues.Add($"unexpected snippet in sequence gap: {negative.Text}");
        }
    }

    private static string NormalizeExpectedCode(string text, RegressionStage? stage)
    {
        if (stage is null)
            return CodeNormalizer.NormalizeAssemblyText(text);

        return CodeNormalizer.NormalizeBladeStage(stage.Value, text);
    }

    private static List<string> EvaluateFlexspin(RegressionFixture fixture, EvaluatedFixture evaluatedFixture)
    {
        List<string> issues = [];
        if (!ShouldRunFlexspin(fixture))
            return issues;

        string? sourceText = evaluatedFixture.FinalAssemblyText;
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            issues.Add("FlexSpin validation was required, but no assembly text was available");
            return issues;
        }

        FlexspinResult result = FlexspinRunner.Run(sourceText);
        if (!result.Succeeded)
        {
            issues.Add("FlexSpin failed:");
            issues.AddRange(result.OutputLines);
        }

        return issues;
    }

    private static HardwareExecutionResult EvaluateHardwareExecution(
        RegressionFixture fixture,
        EvaluatedFixture evaluatedFixture,
        string? hardwarePort,
        HardwareLoaderKind hardwareLoader,
        bool hardwareTurbopropNoVersionCheck)
    {
        bool isPassHw = fixture.Expectation.ExpectationKind == RegressionExpectationKind.PassHw;
        bool isXFailHw = fixture.Expectation.ExpectationKind == RegressionExpectationKind.XFailHw;
        if (fixture.Kind != RegressionFixtureKind.Blade
            || (!isPassHw && !isXFailHw)
            || string.IsNullOrWhiteSpace(hardwarePort))
        {
            return HardwareExecutionResult.NotAttempted();
        }

        if (string.IsNullOrWhiteSpace(evaluatedFixture.FinalAssemblyText))
        {
            return HardwareExecutionResult.Failed(["hardware execution was requested, but no final assembly text was available"]);
        }

        FlexspinBinaryResult binaryResult = FlexspinRunner.BuildBinary(evaluatedFixture.FinalAssemblyText);
        if (!binaryResult.Succeeded)
        {
            List<string> issues = ["hardware binary build failed:"];
            issues.AddRange(binaryResult.OutputLines);
            return HardwareExecutionResult.Failed(issues, binaryResult.BinaryBytes);
        }

        if (binaryResult.BinaryBytes is null)
            return HardwareExecutionResult.Failed(["hardware binary build succeeded, but no output binary was produced"]);

        try
        {
            FixtureConfig config = new()
            {
                ParameterCount = 8,
            };
            List<string> issues = [];
            // For xfail-hw: track whether every run completed and matched (unexpected pass).
            bool allRunsMatchedExpected = fixture.Expectation.HardwareRuns.Count > 0;

            for (int i = 0; i < fixture.Expectation.HardwareRuns.Count; i++)
            {
                HardwareRunExpectation run = fixture.Expectation.HardwareRuns[i];

                Console.Error.WriteLine($"[hw] {fixture.RelativePath} run {i + 1}/{fixture.Expectation.HardwareRuns.Count} {FormatHardwareRunArguments(run)}");

                try
                {
                    uint actualOutput = HardwareFixtureRunner.Run(
                        binaryResult.BinaryBytes,
                        hardwarePort,
                        config,
                        run.Parameters.ToArray(),
                        hardwareLoader,
                        hardwareTurbopropNoVersionCheck);

                    bool runPassed = actualOutput == run.ExpectedOutput;
                    if (isPassHw && !runPassed)
                    {
                        issues.Add(FormatHardwareRunMismatch(i + 1, run, actualOutput));
                        allRunsMatchedExpected = false;
                    }
                    else if (isXFailHw && !runPassed)
                    {
                        // Mismatch is expected — no issue, but not an unexpected pass.
                        allRunsMatchedExpected = false;
                    }
                    // isXFailHw && runPassed: this run matched — leave allRunsMatchedExpected as-is.
                }
                catch (Exception ex)
                {
                    return HardwareExecutionResult.HardwareFailed(
                        [$"hardware run {i + 1} {FormatHardwareRunArguments(run)} failed: {ex.Message}"],
                        binaryResult.BinaryBytes);
                }
            }

            if (isXFailHw && allRunsMatchedExpected)
                issues.Add("all hardware runs unexpectedly produced the correct result");

            return issues.Count == 0
                ? HardwareExecutionResult.Succeeded(binaryResult.BinaryBytes)
                : HardwareExecutionResult.Failed(issues, binaryResult.BinaryBytes);
        }
        catch (Exception ex)
        {
            return HardwareExecutionResult.HardwareFailed(
                [$"hardware execution failed: {ex.Message}"],
                binaryResult.BinaryBytes);
        }
    }

    private static string FormatHardwareRunMismatch(int runIndex, HardwareRunExpectation run, uint actualOutput)
    {
        return
            $"hardware run {runIndex} {FormatHardwareRunArguments(run)} produced an unexpected result:{Environment.NewLine}"
            + FormatHardwareOutputMismatch(run.ExpectedOutput, actualOutput);
    }

    private static string FormatHardwareRunArguments(HardwareRunExpectation run)
    {
        return $"[{string.Join(", ", run.ParameterLiterals)}]";
    }

    private static string FormatHardwareOutputMismatch(uint expectedOutput, uint actualOutput)
    {
        int expectedSigned = unchecked((int)expectedOutput);
        int actualSigned = unchecked((int)actualOutput);

        return string.Format(
            CultureInfo.InvariantCulture,
            """
            hardware output mismatch:
                        hex        | unsigned   |      signed
              expected  0x{0:X8} | {1,10} | {2,11}
              actual    0x{3:X8} | {4,10} | {5,11}
            """,
            expectedOutput,
            expectedOutput,
            expectedSigned,
            actualOutput,
            actualOutput,
            actualSigned);
    }

    private static bool ShouldRunFlexspin(RegressionFixture fixture)
    {
        return fixture.Expectation.FlexspinExpectation switch
        {
            FlexspinExpectation.Required => true,
            FlexspinExpectation.Forbidden => false,
            FlexspinExpectation.Auto => fixture.Expectation.ExpectationKind == RegressionExpectationKind.Pass
                || fixture.Expectation.ExpectationKind == RegressionExpectationKind.PassHw
                || fixture.Expectation.ExpectationKind == RegressionExpectationKind.XFailHw,
            _ => false,
        };
    }

    private static RegressionFixtureOutcome ComputeOutcome(
        RegressionExpectationKind expectationKind,
        bool matched,
        RegressionFixture fixture,
        EvaluatedFixture evaluatedFixture)
    {
        if (expectationKind == RegressionExpectationKind.XFail)
        {
            if (matched)
                return RegressionFixtureOutcome.UnexpectedPass;

            // If the fixture expected error diagnostics but the compiler
            // now produces none, the underlying issue was fixed and the
            // xfail should be promoted — flag it as unexpected pass.
            if (LooksLikeExpectedDiagnosticsResolved(fixture, evaluatedFixture))
                return RegressionFixtureOutcome.UnexpectedPass;

            return RegressionFixtureOutcome.XFail;
        }

        if (expectationKind == RegressionExpectationKind.XFailHw)
        {
            // matched=false (issues present) means all runs unexpectedly passed — unexpected pass.
            // matched=true (no issues) means hardware failed as expected, or was not attempted.
            return matched ? RegressionFixtureOutcome.XFail : RegressionFixtureOutcome.UnexpectedPass;
        }

        return matched ? RegressionFixtureOutcome.Pass : RegressionFixtureOutcome.Fail;
    }

    /// <summary>
    /// Detects when an xfail fixture expected error diagnostics but the
    /// compiler now produces none — the underlying bug was fixed and the
    /// fixture should be promoted to EXPECT: pass.
    /// </summary>
    private static bool LooksLikeExpectedDiagnosticsResolved(RegressionFixture fixture, EvaluatedFixture evaluatedFixture)
    {
        if (fixture.Kind != RegressionFixtureKind.Blade)
            return false;

        RegressionExpectation expectation = fixture.Expectation;
        bool expectedErrors = expectation.LooseDiagnosticCodes.Any(code => code.StartsWith('E'))
            || expectation.ExactDiagnostics.Any(diag => diag.Code.StartsWith('E'));

        return expectedErrors && !evaluatedFixture.Diagnostics.Any(diag => diag.Code.StartsWith('E'));
    }

    private static string BuildSummary(
        RegressionExpectation expectation,
        EvaluatedFixture evaluatedFixture,
        RegressionFixtureOutcome outcome,
        IReadOnlyList<string> issues)
    {
        if (outcome == RegressionFixtureOutcome.Pass)
            return "passed";
        if (outcome == RegressionFixtureOutcome.XFail)
            return "expected failure observed";
        if (outcome == RegressionFixtureOutcome.UnexpectedPass)
            return "unexpected pass";
        if (outcome == RegressionFixtureOutcome.HwFailed)
            return issues.Count > 0 ? issues[0] : "hardware runner failed";

        if (issues.Count > 0)
            return issues[0];

        return evaluatedFixture.Diagnostics.Count > 0
            ? "diagnostic expectations were not met"
            : "fixture failed";
    }

    internal static string StageName(RegressionStage stage)
    {
        return stage switch
        {
            RegressionStage.Bound => "bound",
            RegressionStage.MirPreOptimization => "mir-preopt",
            RegressionStage.Mir => "mir",
            RegressionStage.LirPreOptimization => "lir-preopt",
            RegressionStage.Lir => "lir",
            RegressionStage.AsmirPreOptimization => "asmir-preopt",
            RegressionStage.Asmir => "asmir",
            RegressionStage.FinalAsm => "final-asm",
            _ => throw new InvalidOperationException($"Unknown stage '{stage}'."),
        };
    }
}

internal sealed class EvaluatedFixture
{
    public EvaluatedFixture(
        IReadOnlyList<ActualDiagnostic> diagnostics,
        IReadOnlyDictionary<RegressionStage, string> stageOutputs,
        string? finalAssemblyText,
        string bodyText,
        byte[]? hardwareBinary)
    {
        Diagnostics = diagnostics;
        StageOutputs = stageOutputs;
        FinalAssemblyText = finalAssemblyText;
        BodyText = bodyText;
        HardwareBinary = hardwareBinary;
    }

    public IReadOnlyList<ActualDiagnostic> Diagnostics { get; }
    public IReadOnlyDictionary<RegressionStage, string> StageOutputs { get; }
    public string? FinalAssemblyText { get; }
    public string BodyText { get; }
    public byte[]? HardwareBinary { get; }

    public EvaluatedFixture WithHardwareBinary(byte[] hardwareBinary)
    {
        return new EvaluatedFixture(Diagnostics, StageOutputs, FinalAssemblyText, BodyText, hardwareBinary);
    }

    public static EvaluatedFixture ForAssembly(string bodyText)
    {
        return new EvaluatedFixture([], new Dictionary<RegressionStage, string>(), null, bodyText, null);
    }

    public static EvaluatedFixture Empty(string relativePath)
    {
        _ = relativePath;
        return new EvaluatedFixture([], new Dictionary<RegressionStage, string>(), null, string.Empty, null);
    }
}

internal static class RegressionFixtureParser
{
    private static readonly Regex DirectiveRegex = new(
        @"^(?<name>EXPECT|NOTE|DIAGNOSTICS|STAGE|CONTAINS|SEQUENCE|EXACT|FLEXSPIN|ARGS|RUNS):(?<value>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MarkerRegex = new(
        @"^(?<name>[A-Z][A-Z0-9-]*):(?<value>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ExpectDirectiveRegex = new(
        @"^EXPECT:(?<value>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ExactDiagnosticRegex = new(
        @"^(?:L(?<line>\d+)\s*,\s*)?(?<code>[EWI]\d{4})(?:\s*:\s*(?<message>.+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HardwareRunRegex = new(
        @"^\[(?<parameters>[^\]]*)\]\s*=\s*(?<expected>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static RegressionFixture Parse(DiscoveredRegressionFixture discoveredFixture)
    {
        RegressionFixtureKind kind = DetermineFixtureKind(discoveredFixture.AbsolutePath);
        if (kind == RegressionFixtureKind.BladeCrash)
        {
            if (discoveredFixture.PoolExpectation != RegressionPoolExpectation.Encoded)
                throw new InvalidOperationException(".blade.crash fixtures are only valid in encoded pools.");

            return new RegressionFixture(
                discoveredFixture.AbsolutePath,
                discoveredFixture.RelativePath,
                kind,
                string.Empty,
                string.Empty,
                CreateDefaultExpectation(RegressionExpectationKind.Pass));
        }

        string text = File.ReadAllText(discoveredFixture.AbsolutePath);
        RegressionExpectation expectation;
        string bodyText;
        bool requireFailDiagnosticAssertions = false;

        switch (discoveredFixture.PoolExpectation)
        {
            case RegressionPoolExpectation.Accept:
                expectation = CreateDefaultExpectation(RegressionExpectationKind.Pass);
                bodyText = text;
                break;

            case RegressionPoolExpectation.Reject:
                expectation = CreateDefaultExpectation(RegressionExpectationKind.Fail);
                bodyText = text;
                break;

            case RegressionPoolExpectation.Encoded:
                HeaderScanResult headerScan = HeaderScanResult.Scan(text);
                if (headerScan.HasDirectiveHeader)
                {
                    expectation = ParseExpectation(headerScan);
                    requireFailDiagnosticAssertions = true;
                }
                else
                {
                    expectation = CreateDefaultExpectation(RegressionExpectationKind.Pass);
                }

                bodyText = headerScan.BodyText;
                break;

            default:
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"Unsupported regression pool expectation '{discoveredFixture.PoolExpectation}'."));
        }

        ValidateExpectation(expectation, requireFailDiagnosticAssertions);
        return new RegressionFixture(discoveredFixture.AbsolutePath, discoveredFixture.RelativePath, kind, text, bodyText, expectation);
    }

    private static IEnumerable<string> EnumerateExpectedDiagnosticCodes(RegressionExpectation expectation)
    {
        foreach (string code in expectation.LooseDiagnosticCodes)
            yield return code;
        foreach (ExpectedDiagnostic diagnostic in expectation.ExactDiagnostics)
            yield return diagnostic.Code;
    }

    private static RegressionFixtureKind DetermineFixtureKind(string fixturePath)
    {
        if (fixturePath.EndsWith(".blade.crash", StringComparison.Ordinal))
            return RegressionFixtureKind.BladeCrash;

        string extension = Path.GetExtension(fixturePath);
        return extension switch
        {
            ".blade" => RegressionFixtureKind.Blade,
            _ => throw new InvalidOperationException($"Unsupported regression fixture extension '{extension}'."),
        };
    }

    private static RegressionExpectation CreateDefaultExpectation(RegressionExpectationKind expectationKind)
    {
        return new RegressionExpectation(
            expectationKind,
            null,
            [],
            [],
            null,
            [],
            [],
            FlexspinExpectation.Auto,
            [],
            []);
    }

    private static void ValidateExpectation(RegressionExpectation expectation, bool requireFailDiagnosticAssertions)
    {
        if (expectation.HasCodeAssertions && expectation.Stage is null)
            throw new InvalidOperationException("Blade fixtures with code assertions must specify STAGE.");

        if (requireFailDiagnosticAssertions
            && expectation.ExpectationKind == RegressionExpectationKind.Fail
            && !expectation.HasDiagnosticAssertions)
        {
            throw new InvalidOperationException("EXPECT: fail requires at least one DIAGNOSTICS expectation.");
        }

        if (expectation.ExpectationKind != RegressionExpectationKind.PassHw
                && expectation.ExpectationKind != RegressionExpectationKind.XFailHw
                && expectation.HardwareRuns.Count > 0)
            throw new InvalidOperationException("RUNS is only valid with EXPECT: pass-hw or EXPECT: xfail-hw.");

        if (expectation.ExpectationKind == RegressionExpectationKind.PassHw && expectation.HardwareRuns.Count == 0)
            throw new InvalidOperationException("EXPECT: pass-hw requires RUNS.");

        if (expectation.ExpectationKind == RegressionExpectationKind.XFailHw && expectation.HardwareRuns.Count == 0)
            throw new InvalidOperationException("EXPECT: xfail-hw requires RUNS.");

        if ((expectation.ExpectationKind == RegressionExpectationKind.Pass
                || expectation.ExpectationKind == RegressionExpectationKind.PassHw
                || expectation.ExpectationKind == RegressionExpectationKind.XFailHw)
            && EnumerateExpectedDiagnosticCodes(expectation).Any(code => code.StartsWith('E')))
        {
            throw new InvalidOperationException($"EXPECT: {ExpectationName(expectation.ExpectationKind)} cannot be combined with error diagnostic expectations.");
        }
    }

    private static RegressionExpectation ParseExpectation(HeaderScanResult headerScan)
    {
        RegressionExpectationKind expectationKind = RegressionExpectationKind.Pass;
        RegressionStage? stage = null;
        List<SnippetItem> containsSnippets = [];
        List<SnippetItem> sequenceSnippets = [];
        List<string> looseDiagnosticCodes = [];
        List<ExpectedDiagnostic> exactDiagnostics = [];
        FlexspinExpectation flexspinExpectation = FlexspinExpectation.Auto;
        List<string> compilerArgs = [];
        List<HardwareRunExpectation> hardwareRuns = [];
        StringBuilder? exactText = null;
        HeaderBlock? activeBlock = null;

        foreach (HeaderLine line in headerScan.HeaderLines)
        {
            if (!line.IsComment)
            {
                if (activeBlock == HeaderBlock.Exact && exactText is not null)
                    exactText.AppendLine();
                continue;
            }

            string trimmed = line.Content.TrimStart();
            if (trimmed.Length == 0)
            {
                if (activeBlock == HeaderBlock.Exact && exactText is not null)
                    exactText.AppendLine();
                continue;
            }

            Match directiveMatch = DirectiveRegex.Match(trimmed);
            if (directiveMatch.Success)
            {
                string directiveName = directiveMatch.Groups["name"].Value;
                string directiveValue = directiveMatch.Groups["value"].Value.Trim();
                activeBlock = directiveName switch
                {
                    "NOTE" => HeaderBlock.Note,
                    "DIAGNOSTICS" when directiveValue.Length == 0 => HeaderBlock.ExactDiagnostics,
                    "CONTAINS" => HeaderBlock.Contains,
                    "SEQUENCE" => HeaderBlock.Sequence,
                    "EXACT" => HeaderBlock.Exact,
                    "ARGS" => HeaderBlock.Args,
                    "RUNS" => HeaderBlock.Runs,
                    _ => null,
                };

                switch (directiveName)
                {
                    case "EXPECT":
                        expectationKind = directiveValue switch
                        {
                            "pass" => RegressionExpectationKind.Pass,
                            "pass-hw" => RegressionExpectationKind.PassHw,
                            "fail" => RegressionExpectationKind.Fail,
                            "xfail" => RegressionExpectationKind.XFail,
                            "xfail-hw" => RegressionExpectationKind.XFailHw,
                            _ => throw new InvalidOperationException($"Unsupported EXPECT value '{directiveValue}'."),
                        };
                        break;

                    case "NOTE":
                        break;

                    case "DIAGNOSTICS":
                        if (directiveValue.Length > 0)
                            looseDiagnosticCodes.AddRange(ParseLooseDiagnosticCodes(directiveValue));
                        break;

                    case "STAGE":
                        stage = directiveValue switch
                        {
                            "bound" => RegressionStage.Bound,
                            "mir-preopt" => RegressionStage.MirPreOptimization,
                            "mir" => RegressionStage.Mir,
                            "lir-preopt" => RegressionStage.LirPreOptimization,
                            "lir" => RegressionStage.Lir,
                            "asmir-preopt" => RegressionStage.AsmirPreOptimization,
                            "asmir" => RegressionStage.Asmir,
                            "final-asm" => RegressionStage.FinalAsm,
                            _ => throw new InvalidOperationException($"Unsupported STAGE value '{directiveValue}'."),
                        };
                        break;

                    case "CONTAINS":
                        break;

                    case "SEQUENCE":
                        break;

                    case "EXACT":
                        exactText = new StringBuilder();
                        if (directiveValue.Length > 0)
                            exactText.AppendLine(directiveValue);
                        break;

                    case "ARGS":
                        if (directiveValue.Length > 0)
                            compilerArgs.AddRange(SplitHeaderArgs(directiveValue));
                        break;

                    case "RUNS":
                        if (directiveValue.Length > 0)
                            throw new InvalidOperationException("RUNS only supports block form.");
                        break;

                    case "FLEXSPIN":
                        flexspinExpectation = directiveValue switch
                        {
                            "required" => FlexspinExpectation.Required,
                            "forbidden" => FlexspinExpectation.Forbidden,
                            _ => throw new InvalidOperationException($"Unsupported FLEXSPIN value '{directiveValue}'."),
                        };
                        break;
                }

                continue;
            }

            if (activeBlock is null)
            {
                Match markerMatch = MarkerRegex.Match(trimmed);
                if (markerMatch.Success)
                    throw new InvalidOperationException($"Unsupported header directive '{markerMatch.Groups["name"].Value}'.");

                throw new InvalidOperationException("Header comments after EXPECT must use a supported directive or NOTE block.");
            }

            switch (activeBlock.Value)
            {
                case HeaderBlock.Note:
                    break;

                case HeaderBlock.Contains:
                    containsSnippets.Add(ParseSnippetItem(trimmed, "CONTAINS"));
                    break;

                case HeaderBlock.Sequence:
                    sequenceSnippets.Add(ParseSnippetItem(trimmed, "SEQUENCE"));
                    break;

                case HeaderBlock.Exact:
                    if (exactText is null)
                        throw new InvalidOperationException("EXACT block started without a buffer.");
                    exactText.AppendLine(line.Content);
                    break;

                case HeaderBlock.Args:
                    compilerArgs.Add(ParseBulletItem(trimmed, "ARGS"));
                    break;

                case HeaderBlock.Runs:
                    hardwareRuns.Add(ParseHardwareRunExpectation(ParseBulletItem(trimmed, "RUNS")));
                    break;

                case HeaderBlock.ExactDiagnostics:
                    exactDiagnostics.Add(ParseExactDiagnostic(ParseBulletItem(trimmed, "DIAGNOSTICS")));
                    break;

                default:
                    throw new InvalidOperationException($"Unknown header block '{activeBlock.Value}'.");
            }
        }

        string? exact = exactText?.ToString().TrimEnd();
        return new RegressionExpectation(
            expectationKind,
            stage,
            containsSnippets,
            sequenceSnippets,
            exact,
            looseDiagnosticCodes,
            exactDiagnostics,
            flexspinExpectation,
            compilerArgs,
            hardwareRuns);
    }

    private static string ExpectationName(RegressionExpectationKind expectationKind)
    {
        return expectationKind switch
        {
            RegressionExpectationKind.Pass => "pass",
            RegressionExpectationKind.PassHw => "pass-hw",
            RegressionExpectationKind.Fail => "fail",
            RegressionExpectationKind.XFail => "xfail",
            RegressionExpectationKind.XFailHw => "xfail-hw",
            _ => throw new InvalidOperationException($"Unknown expectation kind '{expectationKind}'."),
        };
    }

    private static IReadOnlyList<string> SplitHeaderArgs(string text)
    {
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static IReadOnlyList<string> ParseLooseDiagnosticCodes(string text)
    {
        string[] parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            if (!Regex.IsMatch(part, @"^[EWI]\d{4}$", RegexOptions.CultureInvariant))
                throw new InvalidOperationException($"Invalid diagnostic code '{part}'.");
        }

        return parts;
    }

    private static readonly Regex CountPrefixRegex = new(
        @"^(?<count>\d+)x\s+(?<text>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string ParseBulletItem(string trimmed, string directiveName)
    {
        if (!trimmed.StartsWith('-'))
            throw new InvalidOperationException($"{directiveName} block entries must begin with '-'.");
        return trimmed[1..].TrimStart();
    }

    private static SnippetItem ParseSnippetItem(string trimmed, string directiveName)
    {
        if (trimmed.StartsWith('-'))
            return SnippetItem.Positive(trimmed[1..].TrimStart());

        if (trimmed.StartsWith('!'))
            return SnippetItem.Negative(trimmed[1..].TrimStart());

        Match countMatch = CountPrefixRegex.Match(trimmed);
        if (countMatch.Success)
        {
            int count = int.Parse(countMatch.Groups["count"].Value, CultureInfo.InvariantCulture);
            string text = countMatch.Groups["text"].Value;
            return SnippetItem.ExactCount(text, count);
        }

        throw new InvalidOperationException(
            $"{directiveName} block entries must begin with '-', '!', or a count prefix (e.g. '3x').");
    }

    private static ExpectedDiagnostic ParseExactDiagnostic(string itemText)
    {
        Match match = ExactDiagnosticRegex.Match(itemText);
        if (!match.Success)
            throw new InvalidOperationException($"Invalid DIAGNOSTICS block entry '{itemText}'.");

        int? line = null;
        if (match.Groups["line"].Success)
            line = int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture);

        string? message = null;
        if (match.Groups["message"].Success)
            message = match.Groups["message"].Value;

        return new ExpectedDiagnostic(match.Groups["code"].Value, line, message);
    }

    private static HardwareRunExpectation ParseHardwareRunExpectation(string text)
    {
        Match match = HardwareRunRegex.Match(text);
        if (!match.Success)
            throw new InvalidOperationException($"Invalid RUNS entry '{text}'. Expected '[ ... ] = value'.");

        string parametersText = match.Groups["parameters"].Value.Trim();
        List<string> parameterLiterals = [];
        List<FixtureParameter> parameters = [];
        if (parametersText.Length > 0)
        {
            string[] parts = parametersText.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Any(static part => part.Length == 0))
                throw new InvalidOperationException($"Invalid RUNS entry '{text}'. Parameters must be comma-separated values.");

            foreach (string part in parts)
            {
                parameterLiterals.Add(part);
                parameters.Add(new FixtureParameter(ParseHardwareLiteral(part)));
            }
        }

        if (parameters.Count > 8)
            throw new InvalidOperationException($"Invalid RUNS entry '{text}'. Hardware fixtures support at most 8 parameters.");

        string expectedLiteral = match.Groups["expected"].Value.Trim();
        uint expectedOutput = ParseHardwareLiteral(expectedLiteral);
        return new HardwareRunExpectation(parameters, parameterLiterals, expectedOutput);
    }

    private static uint ParseHardwareLiteral(string text)
    {
        try
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToUInt32(text[2..], 16);

            if (text.Length > 0 && text[0] == '-')
            {
                int value = int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                return unchecked((uint)value);
            }

            return Convert.ToUInt32(text, 10);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new InvalidOperationException($"Invalid hardware literal '{text}'.", ex);
        }
    }

    private enum HeaderBlock
    {
        Note,
        ExactDiagnostics,
        Contains,
        Sequence,
        Exact,
        Args,
        Runs,
    }

    private readonly record struct HeaderLine(bool IsComment, string Content);

    private sealed class HeaderScanResult
    {
        private HeaderScanResult(IReadOnlyList<HeaderLine> headerLines, string bodyText, bool hasDirectiveHeader)
        {
            HeaderLines = headerLines;
            BodyText = bodyText;
            HasDirectiveHeader = hasDirectiveHeader;
        }

        public IReadOnlyList<HeaderLine> HeaderLines { get; }
        public string BodyText { get; }
        public bool HasDirectiveHeader { get; }

        public static HeaderScanResult Scan(string text)
        {
            string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            List<HeaderLine> headerLines = [];
            int bodyStartIndex = 0;
            bool headerStarted = false;

            while (bodyStartIndex < lines.Length)
            {
                string line = lines[bodyStartIndex];
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (headerStarted)
                        break;

                    headerLines.Add(new HeaderLine(false, string.Empty));
                    bodyStartIndex++;
                    continue;
                }

                if (TryStripCommentPrefix(line, out string? content))
                {
                    headerLines.Add(new HeaderLine(true, content));
                    headerStarted |= ExpectDirectiveRegex.IsMatch(content.TrimStart());
                    bodyStartIndex++;
                    continue;
                }

                break;
            }

            bool hasExpectDirective = headerLines.Any(line =>
                line.IsComment
                && ExpectDirectiveRegex.IsMatch(line.Content.TrimStart()));

            bool startsWithExpectDirective = lines.Length > 0
                && TryStripCommentPrefix(lines[0], out string? firstLineContent)
                && ExpectDirectiveRegex.IsMatch(firstLineContent.TrimStart());

            if (hasExpectDirective && !startsWithExpectDirective)
                throw new InvalidOperationException("EXPECT must be the first line of the file.");

            bool hasDirectiveHeader = startsWithExpectDirective;

            string bodyText = hasDirectiveHeader
                ? string.Join('\n', lines.Skip(bodyStartIndex))
                : text;
            return new HeaderScanResult(headerLines, bodyText, hasDirectiveHeader);
        }

        private static bool TryStripCommentPrefix(string line, out string content)
        {
            string trimmedStart = line.TrimStart();
            if (trimmedStart.StartsWith("//", StringComparison.Ordinal))
            {
                int prefixIndex = line.IndexOf("//", StringComparison.Ordinal);
                content = line[(prefixIndex + 2)..];
                if (content.StartsWith(' '))
                    content = content[1..];
                return true;
            }

            content = string.Empty;
            return false;
        }
    }
}

internal static class CodeNormalizer
{
    public static string NormalizeBladeStage(RegressionStage stage, string text)
    {
        return stage switch
        {
            RegressionStage.Bound => RemoveWhitespace(text),
            RegressionStage.MirPreOptimization => RemoveWhitespace(StripSemicolonComments(text)),
            RegressionStage.Mir => RemoveWhitespace(StripSemicolonComments(text)),
            RegressionStage.LirPreOptimization => RemoveWhitespace(StripSemicolonComments(text)),
            RegressionStage.Lir => RemoveWhitespace(StripSemicolonComments(text)),
            RegressionStage.AsmirPreOptimization => RemoveWhitespace(StripAssemblyComments(text)),
            RegressionStage.Asmir => RemoveWhitespace(StripAssemblyComments(text)),
            RegressionStage.FinalAsm => RemoveWhitespace(StripAssemblyComments(text)),
            _ => throw new InvalidOperationException($"Unknown stage '{stage}'."),
        };
    }

    public static string NormalizeAssemblyText(string text)
    {
        return RemoveWhitespace(StripAssemblyComments(text));
    }

    private static string StripSemicolonComments(string text)
    {
        StringBuilder builder = new();
        foreach (string line in SplitLines(text))
        {
            int commentIndex = line.IndexOf(';', StringComparison.Ordinal);
            string kept = commentIndex >= 0 ? line[..commentIndex] : line;
            builder.AppendLine(kept);
        }

        return builder.ToString();
    }

    private static string StripAssemblyComments(string text)
    {
        StringBuilder builder = new();
        foreach (string line in SplitLines(text))
        {
            string kept = StripComment(line, "'");
            kept = StripComment(kept, ";");
            builder.AppendLine(kept);
        }

        return builder.ToString();
    }

    private static string StripComment(string text, string token)
    {
        int commentIndex = text.IndexOf(token, StringComparison.Ordinal);
        return commentIndex >= 0 ? text[..commentIndex] : text;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    }

    private static string RemoveWhitespace(string text)
    {
        StringBuilder builder = new();
        foreach (char ch in text)
        {
            if (!char.IsWhiteSpace(ch))
                builder.Append(ch);
        }

        return builder.ToString();
    }
}

internal static class SnippetMatcher
{
    private static readonly Regex WildcardTokenRegex = new(
        @"\?(\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool ContainsWildcard(string snippet)
    {
        return snippet.Contains('?', StringComparison.Ordinal);
    }

    public static bool Contains(string haystack, string normalizedSnippet, Dictionary<int, string>? bindings, bool wildcardsEnabled)
    {
        if (!wildcardsEnabled || !ContainsWildcard(normalizedSnippet))
            return haystack.Contains(normalizedSnippet, StringComparison.Ordinal);

        Regex regex = BuildWildcardRegex(normalizedSnippet, bindings);
        Match match = regex.Match(haystack);
        if (!match.Success)
            return false;

        if (bindings is not null)
            RecordBindings(match, bindings);

        return true;
    }

    public static int IndexOf(string haystack, string normalizedSnippet, int startIndex, Dictionary<int, string>? bindings, bool wildcardsEnabled, out int matchLength)
    {
        if (!wildcardsEnabled || !ContainsWildcard(normalizedSnippet))
        {
            matchLength = normalizedSnippet.Length;
            return haystack.IndexOf(normalizedSnippet, startIndex, StringComparison.Ordinal);
        }

        Regex regex = BuildWildcardRegex(normalizedSnippet, bindings);
        Match match = regex.Match(haystack, startIndex);
        if (!match.Success)
        {
            matchLength = 0;
            return -1;
        }

        if (bindings is not null)
            RecordBindings(match, bindings);

        matchLength = match.Length;
        return match.Index;
    }

    public static int CountOccurrences(string haystack, string normalizedSnippet, Dictionary<int, string>? bindings, bool wildcardsEnabled)
    {
        if (!wildcardsEnabled || !ContainsWildcard(normalizedSnippet))
        {
            int count = 0;
            int index = 0;
            while (true)
            {
                int found = haystack.IndexOf(normalizedSnippet, index, StringComparison.Ordinal);
                if (found < 0)
                    break;

                count++;
                index = found + normalizedSnippet.Length;
            }

            return count;
        }

        Regex regex = BuildWildcardRegex(normalizedSnippet, bindings);
        MatchCollection matches = regex.Matches(haystack);
        if (bindings is not null && matches.Count > 0)
            RecordBindings(matches[0], bindings);

        return matches.Count;
    }

    private static Regex BuildWildcardRegex(string normalizedSnippet, Dictionary<int, string>? bindings)
    {
        StringBuilder pattern = new();
        int lastEnd = 0;

        foreach (Match wildcardMatch in WildcardTokenRegex.Matches(normalizedSnippet))
        {
            if (wildcardMatch.Index > lastEnd)
                pattern.Append(Regex.Escape(normalizedSnippet[lastEnd..wildcardMatch.Index]));

            if (wildcardMatch.Groups[1].Success)
            {
                int number = int.Parse(wildcardMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                if (bindings is not null && bindings.TryGetValue(number, out string? boundValue))
                    pattern.Append(Regex.Escape(boundValue));
                else
                    pattern.Append(CultureInfo.InvariantCulture, $"(?<w{number}>\\w+)");
            }
            else
            {
                pattern.Append(@"\w+");
            }

            lastEnd = wildcardMatch.Index + wildcardMatch.Length;
        }

        if (lastEnd < normalizedSnippet.Length)
            pattern.Append(Regex.Escape(normalizedSnippet[lastEnd..]));

        return new Regex(pattern.ToString(), RegexOptions.CultureInvariant);
    }

    private static void RecordBindings(Match match, Dictionary<int, string> bindings)
    {
        foreach (Group group in match.Groups)
        {
            if (group.Success && group.Name.StartsWith('w'))
            {
                int number = int.Parse(group.Name[1..], CultureInfo.InvariantCulture);
                bindings.TryAdd(number, group.Value);
            }
        }
    }
}

internal static class NormalizedDiffBuilder
{
    public static string Build(string expected, string actual)
    {
        int index = 0;
        int maxCommonLength = Math.Min(expected.Length, actual.Length);
        while (index < maxCommonLength && expected[index] == actual[index])
            index++;

        string expectedSnippet = Slice(expected, index);
        string actualSnippet = Slice(actual, index);
        return $"first difference at offset {index}: expected \"{expectedSnippet}\", got \"{actualSnippet}\"";
    }

    private static string Slice(string text, int index)
    {
        if (index >= text.Length)
            return "<end>";

        int start = Math.Max(0, index - 20);
        int length = Math.Min(40, text.Length - start);
        return text.Substring(start, length);
    }
}

internal sealed class ArtifactWriter
{
    private readonly string _repositoryRootPath;
    private readonly bool _enabled;
    private string? _runRootPath;

    public ArtifactWriter(string repositoryRootPath, bool enabled)
    {
        _repositoryRootPath = repositoryRootPath;
        _enabled = enabled;
    }

    public string? WriteFailureArtifacts(
        RegressionFixture fixture,
        EvaluatedFixture evaluatedFixture,
        string summary,
        IReadOnlyList<string> issues)
    {
        if (!_enabled)
            return null;

        _runRootPath ??= CreateRunRootPath();
        string safeRelativePath = Regex.Replace(fixture.RelativePath, @"[^A-Za-z0-9._-]+", "_", RegexOptions.CultureInvariant);
        string artifactDirectoryPath = Path.Combine(_runRootPath, safeRelativePath);
        Directory.CreateDirectory(artifactDirectoryPath);

        File.WriteAllText(Path.Combine(artifactDirectoryPath, "summary.txt"), summary);
        File.WriteAllLines(Path.Combine(artifactDirectoryPath, "issues.txt"), issues);

        if (fixture.Kind == RegressionFixtureKind.Blade)
        {
            File.WriteAllLines(
                Path.Combine(artifactDirectoryPath, "diagnostics.txt"),
                evaluatedFixture.Diagnostics.Select(diagnostic => diagnostic.Display()));
        }

        if (evaluatedFixture.FinalAssemblyText is not null)
            File.WriteAllText(Path.Combine(artifactDirectoryPath, "final.spin2"), evaluatedFixture.FinalAssemblyText);

        foreach ((RegressionStage stage, string content) in evaluatedFixture.StageOutputs)
        {
            string stageFileName = $"{RegressionRunner.StageName(stage)}.txt";
            File.WriteAllText(Path.Combine(artifactDirectoryPath, stageFileName), content);
        }

        if (evaluatedFixture.HardwareBinary is not null)
            File.WriteAllBytes(Path.Combine(artifactDirectoryPath, "hardware.bin"), evaluatedFixture.HardwareBinary);

        if (fixture.Kind != RegressionFixtureKind.Blade)
            File.WriteAllText(Path.Combine(artifactDirectoryPath, "fixture-body.txt"), fixture.BodyText);

        return artifactDirectoryPath;
    }

    private string CreateRunRootPath()
    {
        string root = Path.Combine(
            _repositoryRootPath,
            ".artifacts",
            "regressions",
            DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        return root;
    }
}

internal static class RegressionConfigurationLoader
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static RegressionSuiteConfiguration Load(RegressionRunOptions options)
    {
        string repositoryRootPath = RepositoryLayout.FindRepositoryRoot(options.RepositoryRootPath, options.ConfigPath);
        string configPath = RepositoryLayout.FindConfigurationPath(repositoryRootPath, options.ConfigPath);
        if (!File.Exists(configPath))
            throw new InvalidOperationException($"Regression config file was not found: {configPath}");

        byte[] jsonBytes = File.ReadAllBytes(configPath);
        ReadOnlyMemory<byte> jsonMemory = jsonBytes;
        if (jsonBytes.Length >= 3
            && jsonBytes[0] == 0xEF
            && jsonBytes[1] == 0xBB
            && jsonBytes[2] == 0xBF)
        {
            jsonMemory = jsonBytes.AsMemory(3);
        }

        using JsonDocument document = JsonDocument.Parse(jsonMemory, JsonOptions);

        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Regression config root must be a JSON object.");

        string configDirectoryPath = Path.GetDirectoryName(configPath)
            ?? throw new InvalidOperationException("Regression config path has no parent directory.");

        List<RegressionPoolConfiguration> pools = LoadPools(root, configDirectoryPath, repositoryRootPath);
        string? hardwareRuntimePath = LoadOptionalFilePath(root, "hardwareRuntimePath", configDirectoryPath);
        string? irCoverageGuardPath = LoadOptionalFilePath(root, "irCoverageGuardPath", configDirectoryPath);

        return new RegressionSuiteConfiguration(
            repositoryRootPath,
            configPath,
            pools,
            hardwareRuntimePath,
            irCoverageGuardPath);
    }

    private static List<RegressionPoolConfiguration> LoadPools(
        JsonElement root,
        string configDirectoryPath,
        string repositoryRootPath)
    {
        if (!root.TryGetProperty("pools", out JsonElement poolsElement))
            throw new InvalidOperationException("Regression config is missing required property 'pools'.");
        if (poolsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Regression config property 'pools' must be an array.");

        List<RegressionPoolConfiguration> pools = [];
        HashSet<string> seenPaths = new(PathComparer.Instance);
        int index = 0;
        foreach (JsonElement poolElement in poolsElement.EnumerateArray())
        {
            if (poolElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"Regression pool at index {index} must be an object."));
            }

            string path = ReadRequiredString(poolElement, "path", index);
            string expect = ReadRequiredString(poolElement, "expect", index);
            string absolutePath = ResolveDirectoryPath(path, configDirectoryPath, index);
            if (!seenPaths.Add(absolutePath))
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"Regression pool path '{path}' is duplicated in regressions.cfg.json."));
            }

            pools.Add(new RegressionPoolConfiguration(
                absolutePath,
                Path.GetRelativePath(repositoryRootPath, absolutePath).Replace('\\', '/'),
                ParsePoolExpectation(expect, index)));
            index++;
        }

        return pools;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName, int index)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Regression pool at index {index} is missing required property '{propertyName}'."));
        }

        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Regression pool at index {index} property '{propertyName}' must be a non-empty string."));
        }

        return property.GetString()!;
    }

    private static RegressionPoolExpectation ParsePoolExpectation(string expect, int index)
    {
        return expect switch
        {
            "accept" => RegressionPoolExpectation.Accept,
            "reject" => RegressionPoolExpectation.Reject,
            "encoded" => RegressionPoolExpectation.Encoded,
            _ => throw new InvalidOperationException(FormattableString.Invariant(
                $"Regression pool at index {index} has unsupported expect value '{expect}'.")),
        };
    }

    private static string ResolveDirectoryPath(string path, string configDirectoryPath, int index)
    {
        string absolutePath = Path.GetFullPath(Path.IsPathRooted(path)
            ? path
            : Path.Combine(configDirectoryPath, path));

        if (!Directory.Exists(absolutePath))
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Regression pool at index {index} points to a missing directory: {path}"));
        }

        return absolutePath;
    }

    private static string? LoadOptionalFilePath(JsonElement root, string propertyName, string configDirectoryPath)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement property))
            return null;

        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Regression config property '{propertyName}' must be a non-empty string when present."));
        }

        string configuredPath = property.GetString()!;
        string absolutePath = Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(configDirectoryPath, configuredPath));

        if (!File.Exists(absolutePath))
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Regression config property '{propertyName}' points to a missing file: {configuredPath}"));
        }

        return absolutePath;
    }
}

internal static class RepositoryLayout
{
    private const string DefaultConfigFileName = "regressions.cfg.json";

    public static string FindRepositoryRoot(string? explicitRootPath, string? explicitConfigPath)
    {
        if (explicitRootPath is not null)
            return Path.GetFullPath(explicitRootPath);

        if (explicitConfigPath is not null)
        {
            string configPath = Path.GetFullPath(explicitConfigPath);
            return Path.GetDirectoryName(configPath)
                ?? throw new InvalidOperationException("Configured regression config path has no parent directory.");
        }

        string configPathFromSearch = FindDefaultConfigurationPath();
        return Path.GetDirectoryName(configPathFromSearch)
            ?? throw new InvalidOperationException("Located regression config path has no parent directory.");
    }

    public static string FindConfigurationPath(string repositoryRootPath, string? explicitConfigPath)
    {
        return explicitConfigPath is not null
            ? Path.GetFullPath(explicitConfigPath)
            : Path.Combine(repositoryRootPath, DefaultConfigFileName);
    }

    private static string FindDefaultConfigurationPath()
    {
        string[] candidates =
        [
            Environment.CurrentDirectory,
            AppContext.BaseDirectory,
        ];

        foreach (string candidate in candidates)
        {
            string? current = Path.GetFullPath(candidate);
            while (current is not null)
            {
                string configPath = Path.Combine(current, DefaultConfigFileName);
                if (File.Exists(configPath))
                    return configPath;

                DirectoryInfo? parent = Directory.GetParent(current);
                current = parent?.FullName;
            }
        }

        throw new InvalidOperationException("Unable to locate regressions.cfg.json.");
    }
}

internal sealed class PathComparer : IEqualityComparer<string>
{
    public static PathComparer Instance { get; } = new();

    private readonly StringComparer comparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private PathComparer()
    {
    }

    public bool Equals(string? x, string? y)
    {
        return comparer.Equals(x, y);
    }

    public int GetHashCode(string obj)
    {
        return comparer.GetHashCode(obj);
    }
}


internal sealed class FlexspinProbeResult
{
    public FlexspinProbeResult(bool isAvailable, string probeSummary)
    {
        IsAvailable = isAvailable;
        ProbeSummary = probeSummary;
    }

    public bool IsAvailable { get; }
    public string ProbeSummary { get; }
}

internal sealed class FlexspinResult
{
    public FlexspinResult(bool succeeded, IReadOnlyList<string> outputLines)
    {
        Succeeded = succeeded;
        OutputLines = outputLines;
    }

    public bool Succeeded { get; }
    public IReadOnlyList<string> OutputLines { get; }
}

internal sealed class FlexspinBinaryResult
{
    public FlexspinBinaryResult(bool succeeded, IReadOnlyList<string> outputLines, byte[]? binaryBytes)
    {
        Succeeded = succeeded;
        OutputLines = outputLines;
        BinaryBytes = binaryBytes;
    }

    public bool Succeeded { get; }
    public IReadOnlyList<string> OutputLines { get; }
    public byte[]? BinaryBytes { get; }
}

internal static class FlexspinRunner
{
    public static FlexspinProbeResult ProbeAvailability()
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "flexspin",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--version");

        try
        {
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start flexspin.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            string combined = $"{stdout}\n{stderr}".Trim();
            bool looksValid = !string.IsNullOrWhiteSpace(combined)
                && combined.Contains("flexspin", StringComparison.OrdinalIgnoreCase);
            bool ok = process.ExitCode == 0 && looksValid;
            string summary = ok
                ? combined.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "ok"
                : $"exit={process.ExitCode}; output={combined}";
            return new FlexspinProbeResult(ok, summary);
        }
        catch (Exception ex)
        {
            return new FlexspinProbeResult(false, ex.Message);
        }
    }

    public static FlexspinResult Run(string sourceText)
    {
        return RunCore(sourceText);
    }

    public static FlexspinBinaryResult BuildBinary(string sourceText)
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "blade-regressions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string sourcePath = Path.Combine(tempDirectoryPath, "fixture.spin2");
        string binaryPath = Path.Combine(tempDirectoryPath, "fixture.bin");
        File.WriteAllText(sourcePath, sourceText);

        ProcessStartInfo startInfo = CreateStartInfo();
        startInfo.ArgumentList.Add("-2");
        startInfo.ArgumentList.Add("-b");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(binaryPath);
        startInfo.ArgumentList.Add(sourcePath);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start flexspin.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        List<string> outputLines = CombineOutputLines(stdout, stderr);
        byte[]? binaryBytes = process.ExitCode == 0 && File.Exists(binaryPath)
            ? File.ReadAllBytes(binaryPath)
            : null;

        DeleteTempDirectory(tempDirectoryPath);
        return new FlexspinBinaryResult(process.ExitCode == 0, outputLines, binaryBytes);
    }

    private static FlexspinResult RunCore(string sourceText)
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "blade-regressions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string sourcePath = Path.Combine(tempDirectoryPath, "fixture.spin2");
        File.WriteAllText(sourcePath, sourceText);

        ProcessStartInfo startInfo = CreateStartInfo();
        startInfo.ArgumentList.Add("-2");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("-q");
        startInfo.ArgumentList.Add(sourcePath);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start flexspin.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        List<string> outputLines = CombineOutputLines(stdout, stderr);
        DeleteTempDirectory(tempDirectoryPath);

        return new FlexspinResult(process.ExitCode == 0, outputLines);
    }

    private static ProcessStartInfo CreateStartInfo()
    {
        return new ProcessStartInfo
        {
            FileName = "flexspin",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    private static List<string> CombineOutputLines(string stdout, string stderr)
    {
        return stdout
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
    }

    internal static void DeleteTempDirectory(string tempDirectoryPath)
    {
        try
        {
            Directory.Delete(tempDirectoryPath, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class HardwareExecutionResult
{
    private HardwareExecutionResult(bool attempted, bool hardwareFailed, IReadOnlyList<string> issues, byte[]? binaryBytes)
    {
        Attempted = attempted;
        IsHardwareFailed = hardwareFailed;
        Issues = issues;
        BinaryBytes = binaryBytes;
    }

    public bool Attempted { get; }
    public bool IsHardwareFailed { get; }
    public IReadOnlyList<string> Issues { get; }
    public byte[]? BinaryBytes { get; }

    public static HardwareExecutionResult NotAttempted() => new(false, false, [], null);

    public static HardwareExecutionResult Succeeded(byte[] binaryBytes) => new(true, false, [], binaryBytes);

    public static HardwareExecutionResult Failed(IReadOnlyList<string> issues, byte[]? binaryBytes = null) => new(true, false, issues, binaryBytes);

    public static HardwareExecutionResult HardwareFailed(IReadOnlyList<string> issues, byte[]? binaryBytes = null) => new(true, true, issues, binaryBytes);
}

internal static class HardwareFixtureRunner
{
    public static uint Run(
        byte[] binaryBytes,
        string portName,
        FixtureConfig config,
        FixtureParameter[] parameters,
        HardwareLoaderKind hardwareLoader,
        bool turbopropNoVersionCheck)
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "blade-regressions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string binaryPath = Path.Combine(tempDirectoryPath, "fixture.bin");
        File.WriteAllBytes(binaryPath, binaryBytes);

        try
        {
            Runner runner = new()
            {
                PortName = portName,
                Loader = hardwareLoader,
                TurbopropNoVersionCheck = turbopropNoVersionCheck,
            };
            return runner.Execute(binaryPath, config, parameters);
        }
        finally
        {
            FlexspinRunner.DeleteTempDirectory(tempDirectoryPath);
        }
    }
}

internal static class HardwarePortResolver
{
    // Pass an empty string to explicitly disable hardware (suppress env var lookup).
    public static string? Resolve(string? explicitPort)
    {
        if (explicitPort is not null)
            return string.IsNullOrWhiteSpace(explicitPort) ? null : explicitPort;

        string? envPort = Environment.GetEnvironmentVariable("BLADE_TEST_PORT");
        return string.IsNullOrWhiteSpace(envPort) ? null : envPort;
    }
}

public static class RegressionReportFormatter
{
    public static string Format(RegressionRunResult result)
    {
        Requires.NotNull(result);

        StringBuilder builder = new();
        foreach (RegressionFixtureResult fixtureResult in result.FixtureResults)
        {
            builder.Append(FormatOutcomeLabel(fixtureResult.Outcome).PadRight(14));
            builder.Append(' ');
            builder.AppendLine(fixtureResult.RelativePath);
        }

        builder.AppendLine();

        List<RegressionFixtureResult> expandedResults = result.FixtureResults
            .Where(ShouldExpandDetails)
            .ToList();
        if (expandedResults.Count > 0)
        {
            builder.AppendLine("---");
            builder.AppendLine();

            for (int i = 0; i < expandedResults.Count; i++)
            {
                RegressionFixtureResult fixtureResult = expandedResults[i];
                builder.Append(FormatOutcomeLabel(fixtureResult.Outcome).PadRight(14));
                builder.Append(' ');
                builder.AppendLine(fixtureResult.RelativePath);

                foreach (string detail in EnumerateDetailLines(result.RepositoryRootPath, fixtureResult))
                {
                    builder.Append("  ");
                    builder.AppendLine(detail);
                }

                if (i < expandedResults.Count - 1)
                    builder.AppendLine();
            }

            builder.AppendLine();
        }

        if (result.IrCoverageReport is not null)
        {
            AppendIrCoverage(builder, result.IrCoverageReport);
            builder.AppendLine();
        }

        builder.AppendLine(BuildCompactSummary(result));
        return builder.ToString();
    }

    private static bool ShouldExpandDetails(RegressionFixtureResult fixtureResult)
    {
        return fixtureResult.Outcome is RegressionFixtureOutcome.Fail or RegressionFixtureOutcome.UnexpectedPass or RegressionFixtureOutcome.HwFailed;
    }

    private static string FormatOutcomeLabel(RegressionFixtureOutcome outcome)
    {
        return outcome switch
        {
            RegressionFixtureOutcome.Pass => "PASS",
            RegressionFixtureOutcome.Fail => "FAIL",
            RegressionFixtureOutcome.XFail => "XFAIL",
            RegressionFixtureOutcome.UnexpectedPass => "UNEXPECTED",
            RegressionFixtureOutcome.Skipped => "SKIP",
            RegressionFixtureOutcome.HwFailed => "HW FAILED",
            _ => throw new InvalidOperationException($"Unknown fixture outcome '{outcome}'."),
        };
    }

    private static IEnumerable<string> EnumerateDetailLines(string repositoryRootPath, RegressionFixtureResult fixtureResult)
    {
        bool sawSummary = false;
        foreach (string detail in fixtureResult.Details)
        {
            if (!sawSummary && string.Equals(detail, fixtureResult.Summary, StringComparison.Ordinal))
                sawSummary = true;

            yield return detail;
        }

        if (!sawSummary && fixtureResult.Summary.Length > 0)
            yield return fixtureResult.Summary;

        if (fixtureResult.ArtifactDirectoryPath is not null)
            yield return $"artifacts: {FormatArtifactPath(repositoryRootPath, fixtureResult.ArtifactDirectoryPath)}";
    }

    private static string FormatArtifactPath(string repositoryRootPath, string artifactDirectoryPath)
    {
        string relativePath = Path.GetRelativePath(repositoryRootPath, artifactDirectoryPath);
        return relativePath.Replace('\\', '/');
    }

    private static string BuildCompactSummary(RegressionRunResult result)
    {
        List<string> parts = [];
        if (result.FailCount > 0)
            parts.Add(FormattableString.Invariant($"{result.FailCount} failed"));
        if (result.XFailCount > 0)
            parts.Add(FormattableString.Invariant($"{result.XFailCount} xfailed"));
        if (result.HwFailedCount > 0)
            parts.Add(FormattableString.Invariant($"{result.HwFailedCount} hw-failed"));
        if (result.PassCount > 0)
            parts.Add(FormattableString.Invariant($"{result.PassCount} passed"));

        parts.Add(FormattableString.Invariant($"{result.FixtureResults.Count} total"));
        return string.Join(", ", parts);
    }

    private static void AppendIrCoverage(StringBuilder builder, RegressionIrCoverageReport report)
    {
        foreach (RegressionIrCoverageGroupResult group in report.Groups)
        {
            builder.AppendLine(FormatCoverageSummary(group));
        }

        if (report.RegressionMessages.Count == 0)
            return;

        builder.AppendLine();
        builder.AppendLine("IR coverage regressions:");
        foreach (string message in report.RegressionMessages)
        {
            builder.Append("  ");
            builder.AppendLine(message);
        }
    }

    private static string FormatCoverageSummary(RegressionIrCoverageGroupResult group)
    {
        if (group.UncoveredTypeNames.Count == 0)
            return FormattableString.Invariant($"0 uncovered {group.DisplayName}");

        return FormattableString.Invariant(
            $"{group.UncoveredTypeNames.Count} uncovered {group.DisplayName}: {string.Join(", ", group.UncoveredTypeNames)}");
    }
}

public static class RegressionJsonFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        },
    };

    public static string Format(RegressionRunResult result)
    {
        Requires.NotNull(result);
        return JsonSerializer.Serialize(result, JsonOptions);
    }
}

internal static class RegressionCommandLine
{
    public static RegressionRunOptions Parse(string[] args)
    {
        string? repositoryRootPath = null;
        string? configPath = null;
        string? hardwarePort = null;
        HardwareLoaderKind? hardwareLoader = null;
        bool? turbopropNoVersionCheck = null;
        bool writeFailureArtifacts = true;
        bool json = false;
        List<string> filters = [];

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--repo-root":
                    if (i + 1 >= args.Length)
                        throw new InvalidOperationException("Missing value for --repo-root.");
                    repositoryRootPath = args[++i];
                    break;

                case "--config":
                    if (i + 1 >= args.Length)
                        throw new InvalidOperationException("Missing value for --config.");
                    configPath = args[++i];
                    break;

                case "--no-artifacts":
                    writeFailureArtifacts = false;
                    break;

                case "--json":
                    json = true;
                    break;

                case "--hw-port":
                    if (i + 1 >= args.Length)
                        throw new InvalidOperationException("Missing value for --hw-port.");
                    hardwarePort = args[++i];
                    break;

                case "--hw-loader":
                    if (i + 1 >= args.Length)
                        throw new InvalidOperationException("Missing value for --hw-loader.");
                    hardwareLoader = HardwareLoaderSettings.ParseLoaderKind(args[++i]);
                    break;

                case "--hw-turboprop-no-version-check":
                    turbopropNoVersionCheck = true;
                    break;

                case "--hw-turboprop-version-check":
                    turbopropNoVersionCheck = false;
                    break;

                default:
                    filters.Add(arg);
                    break;
            }
        }

        return new RegressionRunOptions
        {
            RepositoryRootPath = repositoryRootPath,
            ConfigPath = configPath,
            Filters = filters,
            WriteFailureArtifacts = writeFailureArtifacts,
            HardwarePort = HardwarePortResolver.Resolve(hardwarePort),
            HardwareLoader = HardwareLoaderSettings.ResolveLoader(hardwareLoader),
            HardwareTurbopropNoVersionCheck = HardwareLoaderSettings.ResolveTurbopropNoVersionCheck(turbopropNoVersionCheck),
            Json = json,
        };
    }
}
