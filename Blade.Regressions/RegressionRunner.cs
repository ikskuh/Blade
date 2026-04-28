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
    public int OkCount => FixtureResults.Count(result => result.Outcome == RegressionFixtureOutcome.Ok);
    public int FailCount => FixtureResults.Count(result => result.Outcome == RegressionFixtureOutcome.Fail);
    public int XFailCount => FixtureResults.Count(result => result.Outcome == RegressionFixtureOutcome.XFail);
    public int XPassCount => FixtureResults.Count(result => result.Outcome == RegressionFixtureOutcome.XPass);
    public int SkipCount => FixtureResults.Count(result => result.Outcome == RegressionFixtureOutcome.Skipped);
    public int HwFailCount => FixtureResults.Count(result => result.Outcome == RegressionFixtureOutcome.HwFail);
    public int HwErrCount => FixtureResults.Count(result => result.Outcome == RegressionFixtureOutcome.HwErr);
    public bool Succeeded => FailCount == 0
        && HwFailCount == 0
        && HwErrCount == 0
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
    Ok,
    Fail,
    XFail,
    XPass,
    Skipped,
    HwFail,
    HwErr,
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
    XPass,
    XFailHw,
}

internal enum RegressionCompileStatus
{
    Accepted,
    Rejected,
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
        return new SnippetItem(SnippetKind.Count, text, count);
    }
}

public sealed class SnippetBlock
{
    public SnippetBlock(IReadOnlyList<SnippetItem> items)
    {
        Items = items;
    }

    public IReadOnlyList<SnippetItem> Items { get; }
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
        IReadOnlyList<SnippetBlock> containsBlocks,
        IReadOnlyList<SnippetBlock> sequenceBlocks,
        IReadOnlyList<SnippetBlock> exactBlocks,
        IReadOnlyList<string> looseDiagnosticNames,
        IReadOnlyList<ExpectedDiagnostic> exactDiagnostics,
        FlexspinExpectation flexspinExpectation,
        IReadOnlyList<string> compilerArgs,
        IReadOnlyList<HardwareRunExpectation> hardwareRuns)
    {
        ExpectationKind = expectationKind;
        Stage = stage;
        ContainsBlocks = containsBlocks;
        SequenceBlocks = sequenceBlocks;
        ExactBlocks = exactBlocks;
        LooseDiagnosticNames = looseDiagnosticNames;
        ExactDiagnostics = exactDiagnostics;
        FlexspinExpectation = flexspinExpectation;
        CompilerArgs = compilerArgs;
        HardwareRuns = hardwareRuns;
    }

    public RegressionExpectationKind ExpectationKind { get; }
    public RegressionStage? Stage { get; }
    public IReadOnlyList<SnippetBlock> ContainsBlocks { get; }
    public IReadOnlyList<SnippetBlock> SequenceBlocks { get; }
    public IReadOnlyList<SnippetBlock> ExactBlocks { get; }
    public IReadOnlyList<string> LooseDiagnosticNames { get; }
    public IReadOnlyList<ExpectedDiagnostic> ExactDiagnostics { get; }
    public FlexspinExpectation FlexspinExpectation { get; }
    public IReadOnlyList<string> CompilerArgs { get; }
    public IReadOnlyList<HardwareRunExpectation> HardwareRuns { get; }
    public bool HasCodeAssertions => ContainsBlocks.Count > 0 || SequenceBlocks.Count > 0 || ExactBlocks.Count > 0;
    public bool HasDiagnosticAssertions => LooseDiagnosticNames.Count > 0 || ExactDiagnostics.Count > 0;
}

public sealed class ExpectedDiagnostic
{
    public ExpectedDiagnostic(string name, int? line, string? message)
    {
        Name = name;
        Line = line;
        Message = message;
    }

    public string Name { get; }
    public int? Line { get; }
    public string? Message { get; }

    public string Display()
    {
        List<string> parts = [];
        if (Line is not null)
            parts.Add($"L{Line.Value}");
        parts.Add(Name);
        string joined = string.Join(", ", parts);
        if (Message is null)
            return joined;
        return $"{joined}: {Message}";
    }
}

public sealed class ActualDiagnostic
{
    public ActualDiagnostic(string name, DiagnosticSeverity severity, int line, string message)
    {
        Name = name;
        Severity = severity;
        Line = line;
        Message = message;
    }

    public string Name { get; }
    public DiagnosticSeverity Severity { get; }
    public int Line { get; }
    public string Message { get; }

    public string Display() => $"L{Line}, {Name}: {Message}";
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
            try
            {
                fixture = RegressionFixtureParser.Parse(discoveredFixture);
            }
            catch (InvalidOperationException ex)
            {
                return CreateInvalidFixtureResult(discoveredFixture, artifactWriter, ex.Message);
            }

            if (fixture.Kind == RegressionFixtureKind.BladeCrash)
            {
                _ = ExecuteBladeCrashFixture(fixture);
                return new RegressionFixtureResult(relativePath, RegressionFixtureOutcome.Ok, "ok", [], null);
            }

            EvaluatedFixture evaluatedFixture = ExecuteFixture(configuration, fixture, irCoverageSession);
            RegressionCompileStatus compileStatus = DetermineCompileStatus(evaluatedFixture.Diagnostics);
            List<string> nonHardwareIssues = [];
            bool hardwareAttempted = false;

            nonHardwareIssues.AddRange(EvaluateDiagnostics(fixture.Expectation, evaluatedFixture.Diagnostics));
            nonHardwareIssues.AddRange(EvaluateCodeAssertions(fixture, evaluatedFixture));
            if (ShouldRunFlexspin(fixture) && !flexspinProbe.IsAvailable)
            {
                List<string> details =
                [
                    "skipped: flexspin is not available",
                    $"flexspin probe: {flexspinProbe.ProbeSummary}",
                ];
                return new RegressionFixtureResult(relativePath, RegressionFixtureOutcome.Skipped, "skipped", details, null);
            }

            nonHardwareIssues.AddRange(EvaluateFlexspin(fixture, evaluatedFixture));

            bool assertionContractMatched = nonHardwareIssues.Count == 0;
            if (assertionContractMatched && IsHardwareExpectation(fixture.Expectation.ExpectationKind))
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

                List<string> issues = BuildIssuesForOutcome(
                    fixture.Expectation.ExpectationKind,
                    compileStatus,
                    nonHardwareIssues,
                    hardwareExecution);
                RegressionFixtureOutcome hardwareOutcome = ComputeOutcome(
                    fixture.Expectation.ExpectationKind,
                    compileStatus,
                    assertionContractMatched,
                    hardwareExecution);
                string hardwareSummary = BuildSummary(fixture.Expectation, evaluatedFixture, hardwareOutcome, issues);
                string? hardwareArtifactDirectoryPath = null;
                if (ShouldWriteArtifacts(hardwareOutcome))
                {
                    hardwareArtifactDirectoryPath = artifactWriter.WriteFailureArtifacts(fixture, evaluatedFixture, hardwareSummary, issues);
                }

                return new RegressionFixtureResult(
                    relativePath,
                    hardwareOutcome,
                    hardwareSummary,
                    issues,
                    hardwareArtifactDirectoryPath,
                    hardwareAttempted);
            }

            HardwareExecutionResult noHardwareExecution = HardwareExecutionResult.NotAttempted();
            List<string> finalIssues = BuildIssuesForOutcome(
                fixture.Expectation.ExpectationKind,
                compileStatus,
                nonHardwareIssues,
                noHardwareExecution);
            RegressionFixtureOutcome outcome = ComputeOutcome(
                fixture.Expectation.ExpectationKind,
                compileStatus,
                assertionContractMatched,
                noHardwareExecution);
            string summary = BuildSummary(fixture.Expectation, evaluatedFixture, outcome, finalIssues);
            string? artifactDirectoryPath = null;
            if (ShouldWriteArtifacts(outcome))
                artifactDirectoryPath = artifactWriter.WriteFailureArtifacts(fixture, evaluatedFixture, summary, finalIssues);

            return new RegressionFixtureResult(
                relativePath,
                outcome,
                summary,
                finalIssues,
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
                    [],
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

    private static RegressionFixtureResult CreateInvalidFixtureResult(
        DiscoveredRegressionFixture discoveredFixture,
        ArtifactWriter artifactWriter,
        string message)
    {
        RegressionFixture syntheticFixture = new(
            discoveredFixture.AbsolutePath,
            discoveredFixture.RelativePath,
            DetermineFixtureKindOrDefault(discoveredFixture.AbsolutePath),
            text: string.Empty,
            bodyText: string.Empty,
                new RegressionExpectation(
                    RegressionExpectationKind.Pass,
                    null,
                    [],
                    [],
                    [],
                    [],
                    [],
                    FlexspinExpectation.Forbidden,
                    [],
                []));
        EvaluatedFixture emptyFixture = EvaluatedFixture.Empty(discoveredFixture.RelativePath);
        List<string> details = [message];
        string? artifactDirectoryPath = artifactWriter.WriteFailureArtifacts(syntheticFixture, emptyFixture, message, details);
        return new RegressionFixtureResult(
            discoveredFixture.RelativePath,
            RegressionFixtureOutcome.Fail,
            message,
            details,
            artifactDirectoryPath);
    }

    private static RegressionFixtureKind DetermineFixtureKindOrDefault(string fixturePath)
    {
        return fixturePath.EndsWith(".blade.crash", StringComparison.Ordinal)
            ? RegressionFixtureKind.BladeCrash
            : RegressionFixtureKind.Blade;
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
                return new ActualDiagnostic(diag.Name, diag.Severity, location.Line, diag.Message);
            })
            .ToList();

        Dictionary<RegressionStage, string> stageOutputs = [];
        if (compilation.IrBuildResult is not null)
        {
            irCoverageSession?.Record(compilation.IrBuildResult);
            IReadOnlyList<DumpArtifact> dumps = DumpBundleBuilder.Build(
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
            stageOutputs[RegressionStage.Bound] = dumps.Single(static dump => dump.FileName == "00_bound.ir").Content;
            stageOutputs[RegressionStage.MirPreOptimization] = dumps.Single(static dump => dump.FileName == "05_mir_preopt.ir").Content;
            stageOutputs[RegressionStage.Mir] = dumps.Single(static dump => dump.FileName == "10_mir.ir").Content;
            stageOutputs[RegressionStage.LirPreOptimization] = dumps.Single(static dump => dump.FileName == "15_lir_preopt.ir").Content;
            stageOutputs[RegressionStage.Lir] = dumps.Single(static dump => dump.FileName == "20_lir.ir").Content;
            stageOutputs[RegressionStage.AsmirPreOptimization] = dumps.Single(static dump => dump.FileName == "25_asmir_preopt.ir").Content;
            stageOutputs[RegressionStage.Asmir] = dumps.Single(static dump => dump.FileName == "30_asmir.ir").Content;
            stageOutputs[RegressionStage.FinalAsm] = dumps.Single(static dump => dump.FileName == "40_final.spin2").Content;
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

        if (expectation.LooseDiagnosticNames.Count > 0)
        {
            foreach (IGrouping<string, string> group in expectation.LooseDiagnosticNames.GroupBy(name => name, StringComparer.Ordinal))
            {
                int actualCount = diagnostics.Count(diag => diag.Name.Equals(group.Key, StringComparison.Ordinal));
                if (actualCount < group.Count())
                {
                    issues.Add($"missing diagnostic {group.Key}: expected at least {group.Count()}, got {actualCount}");
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

        if (expectation.ExpectationKind == RegressionExpectationKind.Fail
            && !diagnostics.Any(static diag => diag.Severity == DiagnosticSeverity.Error))
            issues.Add("expected at least one error diagnostic, but compilation was clean");

        return issues;
    }

    private static bool MatchesExpectedDiagnostic(ActualDiagnostic actual, ExpectedDiagnostic expected)
    {
        if (!actual.Name.Equals(expected.Name, StringComparison.Ordinal))
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

        NormalizedText normalizedActual = CodeNormalizer.NormalizeBladeStage(expectation.Stage.Value, actualText);
        issues.AddRange(EvaluateNormalizedAssertions(expectation, normalizedActual, expectation.Stage.Value));
        return issues;
    }

    private static List<string> EvaluateNormalizedAssertions(
        RegressionExpectation expectation,
        NormalizedText normalizedActual,
        RegressionStage? stage)
    {
        List<string> issues = [];

        foreach (SnippetBlock block in expectation.ContainsBlocks)
            EvaluateContainsAssertions(block, normalizedActual, stage, issues);

        foreach (SnippetBlock block in expectation.SequenceBlocks)
            EvaluateSequenceAssertions(block, normalizedActual, stage, requireExactGaps: false, issues);

        foreach (SnippetBlock block in expectation.ExactBlocks)
            EvaluateSequenceAssertions(block, normalizedActual, stage, requireExactGaps: true, issues);

        return issues;
    }

    private static void EvaluateContainsAssertions(
        SnippetBlock block,
        NormalizedText normalizedActual,
        RegressionStage? stage,
        List<string> issues)
    {
        PatternBindings bindings = new();
        foreach (SnippetItem item in block.Items)
        {
            Pattern pattern = Pattern.Compile(PrepareExpectedCode(item.Text, stage));

            switch (item.Kind)
            {
                case SnippetKind.Positive:
                    if (!SnippetMatcher.Contains(normalizedActual, pattern, bindings))
                        issues.Add($"missing snippet: {item.Text}");
                    break;

                case SnippetKind.Negative:
                    PatternBindings negativeBindings = bindings.Clone();
                    if (SnippetMatcher.Contains(normalizedActual, pattern, negativeBindings))
                        issues.Add($"unexpected snippet present: {item.Text}");
                    break;

                case SnippetKind.Count:
                    int actualCount = SnippetMatcher.CountOccurrences(normalizedActual, pattern, bindings);
                    if (actualCount != item.Count)
                        issues.Add($"expected {item.Count} occurrence(s) of snippet, found {actualCount}: {item.Text}");
                    break;
            }
        }
    }

    private static void EvaluateSequenceAssertions(
        SnippetBlock block,
        NormalizedText normalizedActual,
        RegressionStage? stage,
        bool requireExactGaps,
        List<string> issues)
    {
        PatternBindings sequenceBindings = new();
        int index = 0;
        int previousPositiveEnd = 0;
        bool sawAdvancingItem = false;
        List<SnippetItem> pendingNegatives = [];

        foreach (SnippetItem item in block.Items)
        {
            Pattern pattern = Pattern.Compile(PrepareExpectedCode(item.Text, stage));

            switch (item.Kind)
            {
                case SnippetKind.Negative:
                    pendingNegatives.Add(item);
                    break;

                case SnippetKind.Positive:
                {
                    if (SnippetMatcher.IndexOf(normalizedActual, pattern, index, sequenceBindings) is not PatternMatch match)
                    {
                        issues.Add($"missing ordered snippet: {item.Text}");
                        return;
                    }

                    if (requireExactGaps && sawAdvancingItem && !CodeNormalizer.IsIgnorableGap(normalizedActual.Text[previousPositiveEnd..match.Start]))
                        issues.Add($"unexpected text between exact snippets before: {item.Text}");

                    CheckPendingNegatives(normalizedActual, previousPositiveEnd, match.Start, pendingNegatives, stage, sequenceBindings, issues);
                    pendingNegatives.Clear();
                    previousPositiveEnd = match.End;
                    index = previousPositiveEnd;
                    sawAdvancingItem = true;
                    break;
                }

                case SnippetKind.Count:
                {
                    int countIndex = index;
                    for (int i = 0; i < item.Count; i++)
                    {
                        if (SnippetMatcher.IndexOf(normalizedActual, pattern, countIndex, sequenceBindings) is not PatternMatch match)
                        {
                            issues.Add($"expected {item.Count} occurrence(s) of ordered snippet, found {i}: {item.Text}");
                            return;
                        }

                        bool firstMatchInItem = i == 0;
                        if (requireExactGaps && (sawAdvancingItem || !firstMatchInItem) && !CodeNormalizer.IsIgnorableGap(normalizedActual.Text[previousPositiveEnd..match.Start]))
                            issues.Add($"unexpected text between exact snippets before: {item.Text}");

                        if (firstMatchInItem)
                        {
                            CheckPendingNegatives(normalizedActual, previousPositiveEnd, match.Start, pendingNegatives, stage, sequenceBindings, issues);
                            pendingNegatives.Clear();
                        }

                        previousPositiveEnd = match.End;
                        countIndex = match.End;
                        sawAdvancingItem = true;
                    }

                    index = countIndex;
                    break;
                }
            }
        }

        if (pendingNegatives.Count > 0)
            CheckPendingNegatives(normalizedActual, previousPositiveEnd, normalizedActual.Text.Length, pendingNegatives, stage, sequenceBindings, issues);
    }

    private static void CheckPendingNegatives(
        NormalizedText normalizedActual,
        int gapStart,
        int gapEnd,
        List<SnippetItem> pendingNegatives,
        RegressionStage? stage,
        PatternBindings bindings,
        List<string> issues)
    {
        if (gapStart >= gapEnd)
            return;

        NormalizedText gap = new(normalizedActual.Text[gapStart..gapEnd]);
        foreach (SnippetItem negative in pendingNegatives)
        {
            Pattern normalizedNeg = Pattern.Compile(PrepareExpectedCode(negative.Text, stage));
            PatternBindings negativeBindings = bindings.Clone();
            if (SnippetMatcher.Contains(gap, normalizedNeg, negativeBindings))
                issues.Add($"unexpected snippet in sequence gap: {negative.Text}");
        }
    }

    private static string PrepareExpectedCode(string text, RegressionStage? stage)
    {
        if (stage is null)
            return CodeNormalizer.StripAssemblyComments(text);

        return CodeNormalizer.StripStageComments(stage.Value, text);
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
            return HardwareExecutionResult.Error(["hardware execution was requested, but no final assembly text was available"]);
        }

        FlexspinBinaryResult binaryResult = FlexspinRunner.BuildBinary(evaluatedFixture.FinalAssemblyText);
        if (!binaryResult.Succeeded)
        {
            List<string> issues = ["hardware binary build failed:"];
            issues.AddRange(binaryResult.OutputLines);
            return HardwareExecutionResult.Error(issues, binaryResult.BinaryBytes);
        }

        if (binaryResult.BinaryBytes is null)
            return HardwareExecutionResult.Error(["hardware binary build succeeded, but no output binary was produced"]);

        try
        {
            FixtureConfig config = new()
            {
                ParameterCount = 8,
            };
            List<string> issues = [];
            bool observedMismatch = false;
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
                        observedMismatch = true;
                    }
                    else if (isXFailHw && !runPassed)
                    {
                        observedMismatch = true;
                        allRunsMatchedExpected = false;
                    }
                    // isXFailHw && runPassed: this run matched — leave allRunsMatchedExpected as-is.
                }
                catch (Exception ex)
                {
                    return HardwareExecutionResult.Error(
                        [$"hardware run {i + 1} {FormatHardwareRunArguments(run)} failed: {ex.Message}"],
                        binaryResult.BinaryBytes);
                }
            }

            if (isXFailHw && allRunsMatchedExpected)
            {
                return HardwareExecutionResult.UnexpectedSuccess(
                    ["all hardware runs unexpectedly produced the correct result"],
                    binaryResult.BinaryBytes);
            }

            if (observedMismatch)
                return HardwareExecutionResult.Mismatch(issues, binaryResult.BinaryBytes);

            return HardwareExecutionResult.Succeeded(binaryResult.BinaryBytes);
        }
        catch (Exception ex)
        {
            return HardwareExecutionResult.Error(
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

    private static RegressionCompileStatus DetermineCompileStatus(IReadOnlyList<ActualDiagnostic> diagnostics)
    {
        return diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            ? RegressionCompileStatus.Rejected
            : RegressionCompileStatus.Accepted;
    }

    private static bool IsHardwareExpectation(RegressionExpectationKind expectationKind)
    {
        return expectationKind is RegressionExpectationKind.PassHw or RegressionExpectationKind.XFailHw;
    }

    private static bool ExpectsAcceptedCompilation(RegressionExpectationKind expectationKind)
    {
        return expectationKind is RegressionExpectationKind.Pass
            or RegressionExpectationKind.PassHw
            or RegressionExpectationKind.XFail
            or RegressionExpectationKind.XFailHw;
    }

    private static bool CompileContractMatched(RegressionExpectationKind expectationKind, RegressionCompileStatus compileStatus)
    {
        bool accepted = compileStatus == RegressionCompileStatus.Accepted;
        return ExpectsAcceptedCompilation(expectationKind) ? accepted : !accepted;
    }

    private static List<string> BuildIssuesForOutcome(
        RegressionExpectationKind expectationKind,
        RegressionCompileStatus compileStatus,
        IReadOnlyList<string> nonHardwareIssues,
        HardwareExecutionResult hardwareExecution)
    {
        List<string> issues = new(nonHardwareIssues);

        if (!CompileContractMatched(expectationKind, compileStatus))
        {
            issues.Insert(0, ExpectsAcceptedCompilation(expectationKind)
                ? "expected compilation to succeed, but it produced error diagnostics"
                : "expected compilation to fail, but it completed without error diagnostics");
        }

        if (hardwareExecution.IsTechnicalError)
        {
            issues.AddRange(hardwareExecution.Issues);
            return issues;
        }

        if (!hardwareExecution.Attempted)
            return issues;

        if (hardwareExecution.Kind == HardwareExecutionKind.UnexpectedSuccess)
        {
            issues.AddRange(hardwareExecution.Issues);
            return issues;
        }

        if (expectationKind == RegressionExpectationKind.PassHw)
        {
            issues.AddRange(hardwareExecution.Issues);
            return issues;
        }

        if (expectationKind == RegressionExpectationKind.XFailHw && hardwareExecution.Kind != HardwareExecutionKind.Mismatch)
        {
            issues.AddRange(hardwareExecution.Issues);
            return issues;
        }

        issues.AddRange(hardwareExecution.Issues);
        return issues;
    }

    private static RegressionFixtureOutcome ComputeOutcome(
        RegressionExpectationKind expectationKind,
        RegressionCompileStatus compileStatus,
        bool assertionContractMatched,
        HardwareExecutionResult hardwareExecution)
    {
        bool compileMatched = CompileContractMatched(expectationKind, compileStatus);
        bool baseContractMatched = compileMatched && assertionContractMatched;

        if (expectationKind == RegressionExpectationKind.Pass)
            return baseContractMatched ? RegressionFixtureOutcome.Ok : RegressionFixtureOutcome.Fail;

        if (expectationKind == RegressionExpectationKind.Fail)
            return baseContractMatched ? RegressionFixtureOutcome.Ok : RegressionFixtureOutcome.Fail;

        if (expectationKind == RegressionExpectationKind.XFail)
        {
            return baseContractMatched ? RegressionFixtureOutcome.Fail : RegressionFixtureOutcome.XFail;
        }

        if (expectationKind == RegressionExpectationKind.XPass)
        {
            return baseContractMatched ? RegressionFixtureOutcome.Fail : RegressionFixtureOutcome.XPass;
        }

        if (expectationKind == RegressionExpectationKind.PassHw)
        {
            if (!baseContractMatched)
                return RegressionFixtureOutcome.Fail;

            if (hardwareExecution.IsTechnicalError)
                return RegressionFixtureOutcome.HwErr;

            if (hardwareExecution.IsMismatch)
                return RegressionFixtureOutcome.HwFail;

            if (hardwareExecution.Kind == HardwareExecutionKind.UnexpectedSuccess)
                return RegressionFixtureOutcome.Fail;

            return RegressionFixtureOutcome.Ok;
        }

        if (expectationKind == RegressionExpectationKind.XFailHw)
        {
            if (!baseContractMatched)
                return RegressionFixtureOutcome.Fail;

            if (hardwareExecution.IsTechnicalError)
                return RegressionFixtureOutcome.HwErr;

            if (hardwareExecution.IsMismatch)
                return RegressionFixtureOutcome.XFail;

            if (!hardwareExecution.Attempted)
                return RegressionFixtureOutcome.Ok;

            return RegressionFixtureOutcome.Fail;
        }

        throw new InvalidOperationException($"Unknown expectation kind '{expectationKind}'.");
    }

    private static bool ShouldWriteArtifacts(RegressionFixtureOutcome outcome)
    {
        return outcome is RegressionFixtureOutcome.Fail
            or RegressionFixtureOutcome.HwFail
            or RegressionFixtureOutcome.HwErr;
    }

    private static string BuildSummary(
        RegressionExpectation expectation,
        EvaluatedFixture evaluatedFixture,
        RegressionFixtureOutcome outcome,
        IReadOnlyList<string> issues)
    {
        if (outcome == RegressionFixtureOutcome.Ok)
            return "ok";
        if (outcome == RegressionFixtureOutcome.Fail)
            return issues.Count > 0 ? issues[0] : "did not meet expectations";
        if (outcome == RegressionFixtureOutcome.XFail)
            return "failed as expected";
        if (outcome == RegressionFixtureOutcome.XPass)
            return "did not fail as expected";
        if (outcome == RegressionFixtureOutcome.HwFail)
            return issues.Count > 0 ? issues[0] : "hardware yielded wrong results";
        if (outcome == RegressionFixtureOutcome.HwErr)
            return issues.Count > 0 ? issues[0] : "hardware execution failed";

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
        @"^(?:L(?<line>\d+)\s*,\s*)?(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\s*:\s*(?<message>.+))?$",
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

    private static IEnumerable<string> EnumerateExpectedDiagnosticNames(RegressionExpectation expectation)
    {
        foreach (string name in expectation.LooseDiagnosticNames)
            yield return name;
        foreach (ExpectedDiagnostic diagnostic in expectation.ExactDiagnostics)
            yield return diagnostic.Name;
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
            [],
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

        if (requireFailDiagnosticAssertions
            && expectation.ExpectationKind == RegressionExpectationKind.XPass
            && !expectation.HasDiagnosticAssertions)
        {
            throw new InvalidOperationException("EXPECT: xpass requires at least one DIAGNOSTICS expectation.");
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
            && EnumerateExpectedDiagnosticNames(expectation)
                .Any(static name => DiagnosticMessage.GetSeverity(name) == DiagnosticSeverity.Error))
        {
            throw new InvalidOperationException($"EXPECT: {ExpectationName(expectation.ExpectationKind)} cannot be combined with error diagnostic expectations.");
        }

        ValidateAdvancingSnippetBlocks(expectation.SequenceBlocks, "SEQUENCE");
        ValidateAdvancingSnippetBlocks(expectation.ExactBlocks, "EXACT");
    }

    private static void ValidateAdvancingSnippetBlocks(IReadOnlyList<SnippetBlock> blocks, string directiveName)
    {
        foreach (SnippetBlock block in blocks)
        {
            if (block.Items.All(static item => item.Kind == SnippetKind.Negative))
                throw new InvalidOperationException($"{directiveName} block requires at least one '-' or count item.");
        }
    }

    private static RegressionExpectation ParseExpectation(HeaderScanResult headerScan)
    {
        RegressionExpectationKind expectationKind = RegressionExpectationKind.Pass;
        RegressionStage? stage = null;
        List<SnippetBlock> containsBlocks = [];
        List<SnippetBlock> sequenceBlocks = [];
        List<SnippetBlock> exactBlocks = [];
        List<string> looseDiagnosticNames = [];
        List<ExpectedDiagnostic> exactDiagnostics = [];
        FlexspinExpectation flexspinExpectation = FlexspinExpectation.Auto;
        List<string> compilerArgs = [];
        List<HardwareRunExpectation> hardwareRuns = [];
        List<SnippetItem>? activeSnippetItems = null;
        HeaderBlock? activeBlock = null;

        foreach (HeaderLine line in headerScan.HeaderLines)
        {
            if (!line.IsComment)
                continue;

            string trimmed = line.Content.TrimStart();
            if (trimmed.Length == 0)
                continue;

            Match directiveMatch = DirectiveRegex.Match(trimmed);
            if (directiveMatch.Success)
            {
                string directiveName = directiveMatch.Groups["name"].Value;
                string directiveValue = directiveMatch.Groups["value"].Value.Trim();
                activeSnippetItems = null;
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
                            "xpass" => RegressionExpectationKind.XPass,
                            "xfail-hw" => RegressionExpectationKind.XFailHw,
                            _ => throw new InvalidOperationException($"Unsupported EXPECT value '{directiveValue}'."),
                        };
                        break;

                    case "NOTE":
                        break;

                    case "DIAGNOSTICS":
                        if (directiveValue.Length > 0)
                            looseDiagnosticNames.AddRange(ParseLooseDiagnosticNames(directiveValue));
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
                        if (directiveValue.Length > 0)
                            throw new InvalidOperationException("CONTAINS only supports block form.");
                        activeSnippetItems = [];
                        containsBlocks.Add(new SnippetBlock(activeSnippetItems));
                        break;

                    case "SEQUENCE":
                        if (directiveValue.Length > 0)
                            throw new InvalidOperationException("SEQUENCE only supports block form.");
                        activeSnippetItems = [];
                        sequenceBlocks.Add(new SnippetBlock(activeSnippetItems));
                        break;

                    case "EXACT":
                        if (directiveValue.Length > 0)
                            throw new InvalidOperationException("EXACT only supports block form.");
                        activeSnippetItems = [];
                        exactBlocks.Add(new SnippetBlock(activeSnippetItems));
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
                    if (activeSnippetItems is null)
                        throw new InvalidOperationException("CONTAINS block started without an item buffer.");
                    activeSnippetItems.Add(ParseSnippetItem(trimmed, "CONTAINS"));
                    break;

                case HeaderBlock.Sequence:
                    if (activeSnippetItems is null)
                        throw new InvalidOperationException("SEQUENCE block started without an item buffer.");
                    activeSnippetItems.Add(ParseSnippetItem(trimmed, "SEQUENCE"));
                    break;

                case HeaderBlock.Exact:
                    if (activeSnippetItems is null)
                        throw new InvalidOperationException("EXACT block started without an item buffer.");
                    activeSnippetItems.Add(ParseSnippetItem(trimmed, "EXACT"));
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

        return new RegressionExpectation(
            expectationKind,
            stage,
            containsBlocks,
            sequenceBlocks,
            exactBlocks,
            looseDiagnosticNames,
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
            RegressionExpectationKind.XPass => "xpass",
            RegressionExpectationKind.XFailHw => "xfail-hw",
            _ => throw new InvalidOperationException($"Unknown expectation kind '{expectationKind}'."),
        };
    }

    private static IReadOnlyList<string> SplitHeaderArgs(string text)
    {
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static IReadOnlyList<string> ParseLooseDiagnosticNames(string text)
    {
        string[] parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            if (DiagnosticMessage.GetByName(part) is null)
                throw new InvalidOperationException($"Invalid diagnostic name '{part}'.");
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
            return SnippetItem.Positive(RequireSnippetText(trimmed[1..].TrimStart(), directiveName));

        if (trimmed.StartsWith('!'))
            return SnippetItem.Negative(RequireSnippetText(trimmed[1..].TrimStart(), directiveName));

        Match countMatch = CountPrefixRegex.Match(trimmed);
        if (countMatch.Success)
        {
            int count = int.Parse(countMatch.Groups["count"].Value, CultureInfo.InvariantCulture);
            if (count == 0)
                throw new InvalidOperationException($"{directiveName} count prefixes must be greater than zero. Use '!' for negative assertions.");

            string text = RequireSnippetText(countMatch.Groups["text"].Value, directiveName);
            return SnippetItem.ExactCount(text, count);
        }

        throw new InvalidOperationException(
            $"{directiveName} block entries must begin with '-', '!', or a count prefix (e.g. '3x').");
    }

    private static string RequireSnippetText(string text, string directiveName)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException($"{directiveName} block entries must include snippet text.");

        return text;
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

        string name = match.Groups["name"].Value;
        if (DiagnosticMessage.GetByName(name) is null)
            throw new InvalidOperationException($"Invalid diagnostic name '{name}'.");

        return new ExpectedDiagnostic(name, line, message);
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

internal readonly record struct NormalizedText(string Text);

internal static class CodeNormalizer
{
    private static readonly Regex WordBoundaryRegex = new(
        @"\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static NormalizedText NormalizeBladeStage(RegressionStage stage, string text)
    {
        return stage switch
        {
            RegressionStage.Bound => NormalizeText(StripStageComments(stage, text)),
            RegressionStage.MirPreOptimization => NormalizeText(StripStageComments(stage, text)),
            RegressionStage.Mir => NormalizeText(StripStageComments(stage, text)),
            RegressionStage.LirPreOptimization => NormalizeText(StripStageComments(stage, text)),
            RegressionStage.Lir => NormalizeText(StripStageComments(stage, text)),
            RegressionStage.AsmirPreOptimization => NormalizeText(StripStageComments(stage, text)),
            RegressionStage.Asmir => NormalizeText(StripStageComments(stage, text)),
            RegressionStage.FinalAsm => NormalizeText(StripStageComments(stage, text)),
            _ => throw new InvalidOperationException($"Unknown stage '{stage}'."),
        };
    }

    public static NormalizedText NormalizeAssemblyText(string text)
    {
        return NormalizeText(StripAssemblyComments(text));
    }

    public static bool IsIgnorableGap(string text)
    {
        return text.All(static ch => ch == ' ');
    }

    public static NormalizedText NormalizeText(string text)
    {
        string separated = WordBoundaryRegex.Replace(text, " ");
        string collapsed = WhitespaceRegex.Replace(separated, " ");
        return new NormalizedText(collapsed.Trim());
    }

    public static string StripStageComments(RegressionStage stage, string text)
    {
        return stage switch
        {
            RegressionStage.Bound => StripSemicolonComments(text),
            RegressionStage.MirPreOptimization => StripSemicolonComments(text),
            RegressionStage.Mir => StripSemicolonComments(text),
            RegressionStage.LirPreOptimization => StripSemicolonComments(text),
            RegressionStage.Lir => StripSemicolonComments(text),
            RegressionStage.AsmirPreOptimization => StripAssemblyComments(text),
            RegressionStage.Asmir => StripAssemblyComments(text),
            RegressionStage.FinalAsm => StripAssemblyComments(text),
            _ => throw new InvalidOperationException($"Unknown stage '{stage}'."),
        };
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

    public static string StripAssemblyComments(string text)
    {
        StringBuilder builder = new();
        foreach (string line in SplitLines(text))
        {
            string kept = StripComment(line, "'");
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
}

internal static class SnippetMatcher
{
    private static readonly Regex WildcardTokenRegex = new(
        @"\?(\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool Contains(NormalizedText haystack, Pattern pattern, PatternBindings bindings)
    {
        return IndexOf(haystack, pattern, 0, bindings) is not null;
    }

    public static PatternMatch? IndexOf(NormalizedText haystack, Pattern pattern, int startIndex, PatternBindings bindings)
    {
        for (int index = startIndex; index <= haystack.Text.Length; index++)
        {
            PatternBindings candidateBindings = bindings.Clone();
            if (pattern.TryMatchAt(haystack.Text, index, candidateBindings, out int end))
            {
                bindings.ReplaceWith(candidateBindings);
                return new PatternMatch(index, end);
            }
        }

        return null;
    }

    public static int CountOccurrences(NormalizedText haystack, Pattern pattern, PatternBindings bindings)
    {
        int count = 0;
        int index = 0;
        PatternBindings countBindings = bindings.Clone();
        while (index <= haystack.Text.Length)
        {
            if (IndexOf(haystack, pattern, index, countBindings) is not PatternMatch match)
                break;

            count++;
            index = match.End;
        }

        if (count > 0)
            bindings.ReplaceWith(countBindings);
        return count;
    }
}

internal readonly record struct PatternMatch(int Start, int End);

internal sealed class PatternBindings
{
    private readonly Dictionary<int, string> _bindings;

    public PatternBindings()
    {
        _bindings = new Dictionary<int, string>();
    }

    private PatternBindings(Dictionary<int, string> bindings)
    {
        _bindings = bindings;
    }

    public PatternBindings Clone()
    {
        return new PatternBindings(new Dictionary<int, string>(_bindings));
    }

    public bool TryBind(int number, string value)
    {
        if (_bindings.TryGetValue(number, out string? bound))
            return string.Equals(bound, value, StringComparison.Ordinal);

        _bindings.Add(number, value);
        return true;
    }

    public void ReplaceWith(PatternBindings other)
    {
        _bindings.Clear();
        foreach ((int key, string value) in other._bindings)
            _bindings.Add(key, value);
    }
}

internal sealed class Pattern
{
    private static readonly Regex WildcardTokenRegex = new(
        @"\?(\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public Pattern(string[] sequences, int?[] patterns)
    {
        Requires.NotNull(sequences);
        Requires.NotNull(patterns);
        Requires.That(sequences.Length == patterns.Length + 1);
        Requires.That(sequences.Skip(1).Take(sequences.Length - 2).All(static sequence => sequence.Length > 0));
        Sequences = sequences;
        Patterns = patterns;
    }

    public string[] Sequences { get; }
    public int?[] Patterns { get; }

    public static Pattern Compile(string text)
    {
        List<string> sequences = [];
        List<int?> patterns = [];
        int lastEnd = 0;
        MatchCollection wildcardMatches = WildcardTokenRegex.Matches(text);

        foreach (Match wildcardMatch in wildcardMatches)
        {
            sequences.Add(NormalizePatternSequence(text[lastEnd..wildcardMatch.Index], sequences.Count, wildcardMatches.Count));
            int? binding = wildcardMatch.Groups[1].Success
                ? int.Parse(wildcardMatch.Groups[1].Value, CultureInfo.InvariantCulture)
                : null;
            patterns.Add(binding);
            lastEnd = wildcardMatch.Index + wildcardMatch.Length;
        }

        sequences.Add(NormalizePatternSequence(text[lastEnd..], sequences.Count, wildcardMatches.Count));
        return new Pattern(sequences.ToArray(), patterns.ToArray());
    }

    public bool TryMatchAt(string text, int start, PatternBindings bindings, out int end)
    {
        end = start;
        if (!MatchesLiteral(text, Sequences[0], end, isFirstSequence: true, out end))
            return false;

        for (int patternIndex = 0; patternIndex < Patterns.Length; patternIndex++)
        {
            if (!TryConsumeIdentifier(text, end, out string identifier, out end))
                return false;

            int? bindingNumber = Patterns[patternIndex];
            if (bindingNumber is not null && !bindings.TryBind(bindingNumber.Value, identifier))
                return false;

            if (!MatchesLiteral(text, Sequences[patternIndex + 1], end, isFirstSequence: false, out end))
                return false;
        }

        return HasTrailingBoundary(text, end);
    }

    private static string NormalizePatternSequence(string text, int sequenceIndex, int patternCount)
    {
        string sequence = CodeNormalizer.NormalizeText(text).Text;
        bool beforeWildcard = sequenceIndex < patternCount;
        bool afterWildcard = sequenceIndex > 0;

        if (sequence.Length == 0)
        {
            if (beforeWildcard && afterWildcard)
                return " ";

            return string.Empty;
        }

        if (afterWildcard && !sequence.StartsWith(' '))
            sequence = " " + sequence;

        if (beforeWildcard && !sequence.EndsWith(' '))
            sequence += " ";

        return sequence;
    }

    private static bool MatchesLiteral(string text, string literal, int start, bool isFirstSequence, out int end)
    {
        end = start;
        if (literal.Length == 0)
            return true;

        if (start + literal.Length > text.Length)
            return false;

        if (!text.AsSpan(start, literal.Length).SequenceEqual(literal.AsSpan()))
            return false;

        if (isFirstSequence && IsWordAtStart(literal) && start > 0 && IsIdentifierChar(text[start - 1]))
            return false;

        end = start + literal.Length;
        return true;
    }

    private static bool HasTrailingBoundary(string text, int end)
    {
        if (end == 0 || end >= text.Length)
            return true;

        if (!IsIdentifierChar(text[end - 1]))
            return true;

        return !IsIdentifierChar(text[end]);
    }

    private static bool IsWordAtStart(string literal)
    {
        return literal.Length > 0 && IsIdentifierChar(literal[0]);
    }

    private static bool TryConsumeIdentifier(string text, int start, out string identifier, out int end)
    {
        identifier = string.Empty;
        end = start;
        if (start >= text.Length || !IsIdentifierChar(text[start]))
            return false;

        while (end < text.Length && IsIdentifierChar(text[end]))
            end++;

        identifier = text[start..end];
        return true;
    }

    private static bool IsIdentifierChar(char ch)
    {
        return char.IsLetterOrDigit(ch) || ch == '_';
    }
}

internal sealed class ArtifactWriter
{
    private const int MaxRunRoots = 10;
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
        string regressionsRoot = Path.Combine(
            _repositoryRootPath,
            ".artifacts",
            "regressions");
        Directory.CreateDirectory(regressionsRoot);

        string root = Path.Combine(
            regressionsRoot,
            DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        PruneRunRoots(regressionsRoot);
        return root;
    }

    private static void PruneRunRoots(string regressionsRoot)
    {
        FileSystemInfo[] staleEntries = new DirectoryInfo(regressionsRoot)
            .EnumerateFileSystemInfos()
            .OrderByDescending(static entry => entry.Name, StringComparer.Ordinal)
            .Skip(MaxRunRoots)
            .ToArray();

        foreach (FileSystemInfo staleEntry in staleEntries)
        {
            switch (staleEntry)
            {
                case DirectoryInfo staleDirectory:
                    staleDirectory.Delete(recursive: true);
                    break;

                default:
                    staleEntry.Delete();
                    break;
            }
        }
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

internal enum HardwareExecutionKind
{
    NotAttempted,
    Succeeded,
    Mismatch,
    UnexpectedSuccess,
    Error,
}

internal sealed class HardwareExecutionResult
{
    private HardwareExecutionResult(HardwareExecutionKind kind, IReadOnlyList<string> issues, byte[]? binaryBytes)
    {
        Kind = kind;
        Issues = issues;
        BinaryBytes = binaryBytes;
    }

    public HardwareExecutionKind Kind { get; }
    public bool Attempted => Kind != HardwareExecutionKind.NotAttempted;
    public bool IsMismatch => Kind == HardwareExecutionKind.Mismatch;
    public bool IsTechnicalError => Kind == HardwareExecutionKind.Error;
    public IReadOnlyList<string> Issues { get; }
    public byte[]? BinaryBytes { get; }

    public static HardwareExecutionResult NotAttempted() => new(HardwareExecutionKind.NotAttempted, [], null);

    public static HardwareExecutionResult Succeeded(byte[] binaryBytes) => new(HardwareExecutionKind.Succeeded, [], binaryBytes);

    public static HardwareExecutionResult Mismatch(IReadOnlyList<string> issues, byte[]? binaryBytes = null) => new(HardwareExecutionKind.Mismatch, issues, binaryBytes);

    public static HardwareExecutionResult UnexpectedSuccess(IReadOnlyList<string> issues, byte[]? binaryBytes = null) => new(HardwareExecutionKind.UnexpectedSuccess, issues, binaryBytes);

    public static HardwareExecutionResult Error(IReadOnlyList<string> issues, byte[]? binaryBytes = null) => new(HardwareExecutionKind.Error, issues, binaryBytes);
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
        return fixtureResult.Outcome is RegressionFixtureOutcome.Fail or RegressionFixtureOutcome.HwFail or RegressionFixtureOutcome.HwErr;
    }

    private static string FormatOutcomeLabel(RegressionFixtureOutcome outcome)
    {
        return outcome switch
        {
            RegressionFixtureOutcome.Ok => "OK",
            RegressionFixtureOutcome.Fail => "FAIL",
            RegressionFixtureOutcome.XFail => "XFAIL",
            RegressionFixtureOutcome.XPass => "XPASS",
            RegressionFixtureOutcome.Skipped => "SKIP",
            RegressionFixtureOutcome.HwFail => "HW FAIL",
            RegressionFixtureOutcome.HwErr => "HW ERR",
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
            parts.Add(FormattableString.Invariant($"{result.FailCount} fail"));
        if (result.HwErrCount > 0)
            parts.Add(FormattableString.Invariant($"{result.HwErrCount} hw err"));
        if (result.XFailCount > 0)
            parts.Add(FormattableString.Invariant($"{result.XFailCount} xfail"));
        if (result.XPassCount > 0)
            parts.Add(FormattableString.Invariant($"{result.XPassCount} xpass"));
        if (result.HwFailCount > 0)
            parts.Add(FormattableString.Invariant($"{result.HwFailCount} hw fail"));
        if (result.OkCount > 0)
            parts.Add(FormattableString.Invariant($"{result.OkCount} ok"));

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
