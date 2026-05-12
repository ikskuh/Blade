using System;
using System.Buffers;
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
using Blade.Reports;
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

public sealed record class RegressionRunResult(
    string RepositoryRootPath,
    IReadOnlyList<RegressionFixtureResult> FixtureResults,
    RegressionIrCoverageReport? IrCoverageReport = null)
{
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

public sealed record class RegressionFixtureResult(
    string RelativePath,
    RegressionFixtureOutcome Outcome,
    string Summary,
    IReadOnlyList<string> Details,
    string? ArtifactDirectoryPath,
    bool HardwareAttempted
);

internal enum RegressionCompileStatus
{
    Accepted,
    Rejected,
}

public static class RegressionRunner
{
    private const int HardwareVectorWidth = 8;

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
        List<DiscoveredRegressionFixture> fixtures = RegressionPool.DiscoverFixtures(configuration, effectiveOptions.Filters);
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
                return new RegressionFixtureResult(relativePath, RegressionFixtureOutcome.Ok, "ok", [], null, false);
            }

            EvaluatedFixture evaluatedFixture = ExecuteFixture(configuration, fixture, irCoverageSession);
            RegressionCompileStatus compileStatus = DetermineCompileStatus(evaluatedFixture.Diagnostics);
            List<string> nonHardwareIssues = [];
            bool hardwareAttempted = false;

            nonHardwareIssues.AddRange(EvaluateDiagnostics(fixture.Expectation, evaluatedFixture.Diagnostics));
            CodeAssertionEvaluationResult codeAssertionResult = EvaluateCodeAssertions(fixture, evaluatedFixture);
            nonHardwareIssues.AddRange(codeAssertionResult.Issues);
            if (codeAssertionResult.MatcherTraceReport is not null)
                evaluatedFixture = evaluatedFixture.WithMatcherTrace(codeAssertionResult.MatcherTraceReport);
            if (ShouldRunFlexspin(fixture) && !flexspinProbe.IsAvailable)
            {
                List<string> details =
                [
                    "skipped: flexspin is not available",
                    $"flexspin probe: {flexspinProbe.ProbeSummary}",
                ];
                return new RegressionFixtureResult(relativePath, RegressionFixtureOutcome.Skipped, "skipped", details, null, false);
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
                    hardwareArtifactDirectoryPath = artifactWriter.WriteFailureArtifacts(
                        fixture,
                        evaluatedFixture,
                        hardwareSummary,
                        issues,
                        hardwareExecution.CompletedRuns);
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
            return new RegressionFixtureResult(relativePath, RegressionFixtureOutcome.Fail, summary, details, artifactDirectoryPath, false);
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
            artifactDirectoryPath,
            false);
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
        CompilationOutput compilation = CompilerDriver.Compile(fixture.Text, fixture.AbsolutePath, options);
        List<ActualDiagnostic> diagnostics = compilation.Diagnostics
            .Select(diag =>
            {
                SourceLocation location = diag.GetLocation();
                return new ActualDiagnostic(diag.Name, diag.Severity, location.Line, diag.Message);
            })
            .ToList();

        Dictionary<RegressionStage, string> stageOutputs = [];
        string? assemblyText = compilation.Status == CompilationStatus.Succeeded && compilation.Stages.IsComplete
            ? compilation.Stages.RenderAssemblyText()
            : null;
        if (assemblyText is not null)
        {
            irCoverageSession?.Record(compilation);
            IReadOnlyList<ReportSection> dumps = ReportSectionCatalog.BuildSections(compilation);
            stageOutputs[RegressionStage.Bound] = dumps.Single(static dump => dump.FileName == "00_bound.ir").RenderPlainText();
            stageOutputs[RegressionStage.MirPreOptimization] = dumps.Single(static dump => dump.FileName == "05_mir_preopt.ir").RenderPlainText();
            stageOutputs[RegressionStage.Mir] = dumps.Single(static dump => dump.FileName == "10_mir.ir").RenderPlainText();
            stageOutputs[RegressionStage.LirPreOptimization] = dumps.Single(static dump => dump.FileName == "15_lir_preopt.ir").RenderPlainText();
            stageOutputs[RegressionStage.Lir] = dumps.Single(static dump => dump.FileName == "20_lir.ir").RenderPlainText();
            stageOutputs[RegressionStage.AsmirPreOptimization] = dumps.Single(static dump => dump.FileName == "25_asmir_preopt.ir").RenderPlainText();
            stageOutputs[RegressionStage.Asmir] = dumps.Single(static dump => dump.FileName == "30_asmir.ir").RenderPlainText();
            stageOutputs[RegressionStage.FinalAsm] = assemblyText;
        }

        return new EvaluatedFixture(
            diagnostics,
            stageOutputs,
            assemblyText,
            fixture.BodyText,
            null,
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

    private static CodeAssertionEvaluationResult EvaluateCodeAssertions(RegressionFixture fixture, EvaluatedFixture evaluatedFixture)
    {
        List<string> issues = [];
        RegressionExpectation expectation = fixture.Expectation;
        if (!expectation.HasCodeAssertions)
            return new CodeAssertionEvaluationResult(issues, null);

        if (fixture.Kind != RegressionFixtureKind.Blade)
        {
            issues.Add("only .blade fixtures support code assertions");
            return new CodeAssertionEvaluationResult(issues, null);
        }

        if (expectation.Stage is null)
        {
            issues.Add("fixture has code assertions but no STAGE");
            return new CodeAssertionEvaluationResult(issues, null);
        }

        if (!evaluatedFixture.StageOutputs.TryGetValue(expectation.Stage.Value, out string? actualText))
        {
            issues.Add($"requested stage '{StageName(expectation.Stage.Value)}' is unavailable");
            return new CodeAssertionEvaluationResult(issues, null);
        }

        NormalizedSourceText normalizedActual = CodeNormalizer.NormalizeBladeStage(expectation.Stage.Value, actualText);
        return EvaluateNormalizedAssertions(expectation, normalizedActual, expectation.Stage.Value);
    }

    private static CodeAssertionEvaluationResult EvaluateNormalizedAssertions(
        RegressionExpectation expectation,
        NormalizedSourceText normalizedActual,
        RegressionStage? stage)
    {
        List<string> issues = [];
        List<MatcherTraceBlock> blocks = [];

        int containsBlockNumber = 1;
        foreach (SnippetBlock block in expectation.ContainsBlocks)
        {
            blocks.Add(EvaluateContainsAssertions(block, normalizedActual, stage, containsBlockNumber, issues));
            containsBlockNumber++;
        }

        int sequenceBlockNumber = 1;
        foreach (SnippetBlock block in expectation.SequenceBlocks)
        {
            blocks.Add(EvaluateSequenceAssertions(
                block,
                normalizedActual,
                stage,
                requireExactGaps: false,
                MatcherTraceBlockKind.Sequence,
                sequenceBlockNumber,
                issues));
            sequenceBlockNumber++;
        }

        int exactBlockNumber = 1;
        foreach (SnippetBlock block in expectation.ExactBlocks)
        {
            blocks.Add(EvaluateSequenceAssertions(
                block,
                normalizedActual,
                stage,
                requireExactGaps: true,
                MatcherTraceBlockKind.Exact,
                exactBlockNumber,
                issues));
            exactBlockNumber++;
        }

        return new CodeAssertionEvaluationResult(issues, new MatcherTraceReport(stage, blocks));
    }

    private static MatcherTraceBlock EvaluateContainsAssertions(
        SnippetBlock block,
        NormalizedSourceText normalizedActual,
        RegressionStage? stage,
        int blockNumber,
        List<string> issues)
    {
        PatternBindings bindings = new();
        List<MatcherTraceItem> itemTraces = [];
        string? failureReason = null;

        for (int itemNumber = 0; itemNumber < block.Items.Count; itemNumber++)
        {
            SnippetItem item = block.Items[itemNumber];
            List<MatcherTraceMatch> matches = [];
            bool succeeded = true;
            string? itemFailureReason = null;

            switch (item.Kind)
            {
                case SnippetKind.Positive:
                    {
                        if (SnippetMatcher.IndexOf(normalizedActual, item.Pattern, 0, bindings) is not PatternMatch match)
                        {
                            itemFailureReason = $"missing snippet: {item.Pattern}";
                            issues.Add(itemFailureReason);
                            succeeded = false;
                            failureReason ??= itemFailureReason;
                        }
                        else
                        {
                            matches.Add(CreateTraceMatch(normalizedActual, match, bindings));
                        }

                        break;
                    }

                case SnippetKind.Negative:
                    {
                        PatternBindings negativeBindings = bindings.Clone();
                        if (SnippetMatcher.IndexOf(normalizedActual, item.Pattern, 0, negativeBindings) is PatternMatch match)
                        {
                            itemFailureReason = $"unexpected snippet present: {item.Pattern}";
                            issues.Add(itemFailureReason);
                            succeeded = false;
                            failureReason ??= itemFailureReason;
                            matches.Add(CreateTraceMatch(normalizedActual, match, negativeBindings));
                        }

                        break;
                    }

                case SnippetKind.Count:
                    {
                        int index = 0;
                        PatternBindings countBindings = bindings.Clone();
                        while (index < normalizedActual.LineCount)
                        {
                            if (SnippetMatcher.IndexOf(normalizedActual, item.Pattern, index, countBindings) is not PatternMatch match)
                                break;

                            matches.Add(CreateTraceMatch(normalizedActual, match, countBindings));
                            index = match.EndLineIndexExclusive;
                        }

                        if (matches.Count != item.Count)
                        {
                            itemFailureReason = $"expected {item.Count} occurrence(s) of snippet, found {matches.Count}: {item.Pattern}";
                            issues.Add(itemFailureReason);
                            succeeded = false;
                            failureReason ??= itemFailureReason;
                        }

                        if (matches.Count > 0)
                            bindings.ReplaceWith(countBindings);
                        break;
                    }

                default:
                    throw new UnreachableException();
            }

            itemTraces.Add(new MatcherTraceItem(
                itemNumber + 1,
                item,
                matches,
                bindings.Snapshot(),
                succeeded,
                itemFailureReason,
                0,
                null,
                null,
                null));
        }

        return new MatcherTraceBlock(
            MatcherTraceBlockKind.Contains,
            blockNumber,
            itemTraces,
            bindings.Snapshot(),
            null,
            failureReason is null,
            failureReason);
    }

    private static MatcherTraceBlock EvaluateSequenceAssertions(
        SnippetBlock block,
        NormalizedSourceText normalizedActual,
        RegressionStage? stage,
        bool requireExactGaps,
        MatcherTraceBlockKind blockKind,
        int blockNumber,
        List<string> issues)
    {
        PatternBindings sequenceBindings = new();
        int index = 0;
        int previousPositiveEnd = 0;
        bool sawAdvancingItem = false;
        List<(SnippetItem Item, int ItemNumber)> pendingNegatives = [];
        List<MatcherTraceItem> itemTraces = [];
        string? failureReason = null;

        for (int itemNumber = 0; itemNumber < block.Items.Count; itemNumber++)
        {
            SnippetItem item = block.Items[itemNumber];
            if (item.Kind == SnippetKind.Negative)
            {
                pendingNegatives.Add((item, itemNumber + 1));
                continue;
            }

            if (item.Kind == SnippetKind.Positive)
            {
                if (SnippetMatcher.IndexOf(normalizedActual, item.Pattern, index, sequenceBindings) is not PatternMatch match)
                {
                    string itemFailureReason = $"missing ordered snippet: {item.Pattern}";
                    issues.Add(itemFailureReason);
                    failureReason ??= itemFailureReason;
                    itemTraces.Add(new MatcherTraceItem(
                        itemNumber + 1,
                        item,
                        [],
                        sequenceBindings.Snapshot(),
                        false,
                        itemFailureReason,
                        index,
                        null,
                        null,
                        null));
                    return new MatcherTraceBlock(blockKind, blockNumber, itemTraces, sequenceBindings.Snapshot(), index, false, failureReason);
                }

                string? exactGapFailure = ValidateExactGap(normalizedActual, previousPositiveEnd, match.StartLineIndex, requireExactGaps, sawAdvancingItem, item.Pattern, issues);
                if (exactGapFailure is not null)
                    failureReason ??= exactGapFailure;

                List<MatcherTraceItem> negativeTraces = CheckPendingNegatives(
                    normalizedActual,
                    previousPositiveEnd,
                    match.StartLineIndex,
                    pendingNegatives,
                    stage,
                    sequenceBindings,
                    issues,
                    ref failureReason);
                itemTraces.AddRange(negativeTraces);
                pendingNegatives.Clear();

                itemTraces.Add(new MatcherTraceItem(
                    itemNumber + 1,
                    item,
                    [CreateTraceMatch(normalizedActual, match, sequenceBindings)],
                    sequenceBindings.Snapshot(),
                    exactGapFailure is null,
                    exactGapFailure,
                    index,
                    requireExactGaps && sawAdvancingItem ? previousPositiveEnd : null,
                    requireExactGaps && sawAdvancingItem ? match.StartLineIndex : null,
                    requireExactGaps && sawAdvancingItem ? normalizedActual.GetGapText(previousPositiveEnd, match.StartLineIndex) : null));

                previousPositiveEnd = match.EndLineIndexExclusive;
                index = previousPositiveEnd;
                sawAdvancingItem = true;
                continue;
            }

            if (item.Kind == SnippetKind.Count)
            {
                List<MatcherTraceMatch> matches = [];
                int countIndex = index;
                int gapStartBeforeCount = previousPositiveEnd;
                string? itemFailureReason = null;
                string? exactGapFailure = null;
                int? firstMatchStart = null;

                for (int i = 0; i < item.Count; i++)
                {
                    if (SnippetMatcher.IndexOf(normalizedActual, item.Pattern, countIndex, sequenceBindings) is not PatternMatch match)
                    {
                        itemFailureReason = $"expected {item.Count} occurrence(s) of ordered snippet, found {i}: {item.Pattern}";
                        issues.Add(itemFailureReason);
                        failureReason ??= itemFailureReason;
                        break;
                    }

                    firstMatchStart ??= match.StartLineIndex;
                    bool shouldCheckGap = requireExactGaps && (sawAdvancingItem || i > 0);
                    string? gapFailure = ValidateExactGap(normalizedActual, previousPositiveEnd, match.StartLineIndex, shouldCheckGap, shouldCheckGap, item.Pattern, issues);
                    if (gapFailure is not null)
                    {
                        exactGapFailure ??= gapFailure;
                        failureReason ??= gapFailure;
                    }

                    matches.Add(CreateTraceMatch(normalizedActual, match, sequenceBindings));
                    previousPositiveEnd = match.EndLineIndexExclusive;
                    countIndex = match.EndLineIndexExclusive;
                    sawAdvancingItem = true;
                }

                if (firstMatchStart is not null)
                {
                    List<MatcherTraceItem> negativeTraces = CheckPendingNegatives(
                        normalizedActual,
                        gapStartBeforeCount,
                        firstMatchStart.Value,
                        pendingNegatives,
                        stage,
                        sequenceBindings,
                        issues,
                        ref failureReason);
                    itemTraces.AddRange(negativeTraces);
                }

                pendingNegatives.Clear();

                itemTraces.Add(new MatcherTraceItem(
                    itemNumber + 1,
                    item,
                    matches,
                    sequenceBindings.Snapshot(),
                    itemFailureReason is null && exactGapFailure is null,
                    itemFailureReason ?? exactGapFailure,
                    index,
                    requireExactGaps && matches.Count > 0 ? index : null,
                    requireExactGaps && matches.Count > 0 ? matches[0].LineIndex : null,
                    requireExactGaps && matches.Count > 0 ? normalizedActual.GetGapText(index, matches[0].LineIndex) : null));

                if (itemFailureReason is not null)
                    return new MatcherTraceBlock(blockKind, blockNumber, itemTraces, sequenceBindings.Snapshot(), countIndex, false, failureReason);

                index = countIndex;
                continue;
            }

            throw new UnreachableException();
        }

        if (pendingNegatives.Count > 0)
        {
            List<MatcherTraceItem> negativeTraces = CheckPendingNegatives(
                normalizedActual,
                previousPositiveEnd,
                normalizedActual.LineCount,
                pendingNegatives,
                stage,
                sequenceBindings,
                issues,
                ref failureReason);
            itemTraces.AddRange(negativeTraces);
        }

        return new MatcherTraceBlock(blockKind, blockNumber, itemTraces, sequenceBindings.Snapshot(), index, failureReason is null, failureReason);
    }

    private static List<MatcherTraceItem> CheckPendingNegatives(
        NormalizedSourceText normalizedActual,
        int gapStart,
        int gapEnd,
        List<(SnippetItem Item, int ItemNumber)> pendingNegatives,
        RegressionStage? stage,
        PatternBindings bindings,
        List<string> issues,
        ref string? failureReason)
    {
        List<MatcherTraceItem> traces = [];
        foreach ((SnippetItem negative, int itemNumber) in pendingNegatives)
        {
            Pattern normalizedNeg = negative.Pattern;
            PatternBindings negativeBindings = bindings.Clone();
            List<MatcherTraceMatch> matches = [];
            bool succeeded = true;
            string? itemFailureReason = null;

            if (gapStart < gapEnd)
            {
                if (SnippetMatcher.IndexOf(normalizedActual, normalizedNeg, gapStart, gapEnd, negativeBindings) is PatternMatch absoluteMatch)
                {
                    matches.Add(CreateTraceMatch(normalizedActual, absoluteMatch, negativeBindings));
                    itemFailureReason = $"unexpected snippet in sequence gap: {negative.Pattern}";
                    issues.Add(itemFailureReason);
                    succeeded = false;
                    failureReason ??= itemFailureReason;
                }
            }

            traces.Add(new MatcherTraceItem(
                itemNumber,
                negative,
                matches,
                bindings.Snapshot(),
                succeeded,
                itemFailureReason,
                0,
                gapStart,
                gapEnd,
                normalizedActual.GetGapText(gapStart, gapEnd)));
        }

        return traces;
    }

    private static string? ValidateExactGap(
        NormalizedSourceText normalizedActual,
        int gapStartLineIndex,
        int gapEndLineIndex,
        bool requireExactGaps,
        bool shouldCheckGap,
        Pattern pattern,
        List<string> issues)
    {
        if (!requireExactGaps || !shouldCheckGap)
            return null;

        if (gapStartLineIndex >= gapEndLineIndex)
            return null;

        string issue = $"unexpected text between exact snippets before: {pattern.Source}";
        issues.Add(issue);
        return issue;
    }

    private static MatcherTraceMatch CreateTraceMatch(
        NormalizedSourceText normalizedActual,
        PatternMatch match,
        PatternBindings bindings)
    {
        NormalizedSourceLine line = normalizedActual.GetLine(match.StartLineIndex);
        return new MatcherTraceMatch(
            match.StartLineIndex,
            line.Text.Text,
            line.SourceLineNumber,
            line.Text.Text,
            bindings.Snapshot());
    }

    private static string DescribePattern(Pattern pattern)
    {
        StringBuilder builder = new();
        for (int index = 0; index < pattern.Parts.Count; index++)
        {
            if (builder.Length > 0)
                builder.Append(' ');

            PatternPart part = pattern.Parts[index];
            builder.Append(part.IsWildcard
                ? part.BindingNumber is int bindingNumber
                    ? $"?{bindingNumber.ToString(CultureInfo.InvariantCulture)}"
                    : "?"
                : part.Literal);
        }

        return builder.ToString();
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
                ParameterCount = HardwareVectorWidth,
            };
            List<string> issues = [];
            bool observedMismatch = false;
            bool allRunsMatchedExpected = fixture.Expectation.HardwareRuns.Count > 0;
            List<HardwareRunCapture> completedRuns = [];

            for (int i = 0; i < fixture.Expectation.HardwareRuns.Count; i++)
            {
                HardwareRunExpectation run = fixture.Expectation.HardwareRuns[i];

                Console.Error.WriteLine($"[hw] {fixture.RelativePath} run {i + 1}/{fixture.Expectation.HardwareRuns.Count} {FormatHardwareRunArguments(run)}");

                try
                {
                    TestResult testResult = HardwareFixtureRunner.Run(
                        binaryResult.BinaryBytes,
                        hardwarePort,
                        config,
                        run.Parameters.ToArray(),
                        hardwareLoader,
                        hardwareTurbopropNoVersionCheck);

                    if (testResult.Outputs.Count == 0 || testResult.Outputs.Count > HardwareVectorWidth)
                    {
                        return HardwareExecutionResult.Error(
                            [$"hardware run {i + 1} {FormatHardwareRunArguments(run)} produced {testResult.Outputs.Count} outputs; hardware fixtures support between 1 and 8 outputs"],
                            binaryResult.BinaryBytes,
                            [.. completedRuns]);
                    }

                    TestResult normalizedResult = NormalizeHardwareTestResult(testResult);
                    completedRuns.Add(new HardwareRunCapture(i + 1, run, normalizedResult));

                    bool runPassed = normalizedResult.Outputs.SequenceEqual(run.ExpectedOutputs);
                    if (isPassHw && !runPassed)
                    {
                        issues.Add(FormatHardwareRunMismatch(i + 1, run, normalizedResult.Outputs));
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
                        binaryResult.BinaryBytes,
                        [.. completedRuns]);
                }
            }

            if (isXFailHw && allRunsMatchedExpected)
            {
                return HardwareExecutionResult.UnexpectedSuccess(
                    ["all hardware runs unexpectedly produced the correct result"],
                    binaryResult.BinaryBytes,
                    [.. completedRuns]);
            }

            if (observedMismatch)
                return HardwareExecutionResult.Mismatch(issues, binaryResult.BinaryBytes, [.. completedRuns]);

            return HardwareExecutionResult.Succeeded(binaryResult.BinaryBytes, [.. completedRuns]);
        }
        catch (Exception ex)
        {
            return HardwareExecutionResult.Error(
                [$"hardware execution failed: {ex.Message}"],
                binaryResult.BinaryBytes);
        }
    }

    private static TestResult NormalizeHardwareTestResult(TestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new TestResult(
            ZeroFillHardwareValues(result.Inputs),
            ZeroFillHardwareValues(result.Outputs),
            result.Log,
            result.StdOut,
            result.StdErr);
    }

    private static string FormatHardwareRunMismatch(int runIndex, HardwareRunExpectation run, IReadOnlyList<uint> actualOutputs)
    {
        return
            $"hardware run {runIndex} {FormatHardwareRunArguments(run)} produced an unexpected result:{Environment.NewLine}"
            + FormatHardwareOutputMismatch(run.ExpectedOutputs, actualOutputs);
    }

    private static string FormatHardwareRunArguments(HardwareRunExpectation run)
    {
        return $"[{string.Join(", ", run.ParameterLiterals)}]";
    }

    private static string FormatHardwareOutputMismatch(IReadOnlyList<uint> expectedOutputs, IReadOnlyList<uint> actualOutputs)
    {
        StringBuilder builder = new();
        builder.AppendLine("hardware output mismatch:");
        AppendHardwareValueList(builder, "  expected:", expectedOutputs);
        AppendHardwareValueList(builder, "  actual:", actualOutputs);
        return builder.ToString();
    }

    private static void AppendHardwareValueList(StringBuilder builder, string title, IReadOnlyList<uint> values)
    {
        builder.AppendLine(title);
        for (int i = 0; i < values.Count; i++)
        {
            builder.Append("    [");
            builder.Append(i.ToString(CultureInfo.InvariantCulture));
            builder.Append("] ");
            builder.AppendLine(FormatHardwareValue(values[i]));
        }
    }

    private static string FormatHardwareValue(uint value)
    {
        int signedValue = unchecked((int)value);
        return string.Format(CultureInfo.InvariantCulture, "0x{0:X8} | unsigned {1} | signed {2}", value, value, signedValue);
    }

    private static uint[] ZeroFillHardwareValues(IReadOnlyList<uint> values)
    {
        uint[] padded = new uint[HardwareVectorWidth];
        int copyCount = Math.Min(values.Count, HardwareVectorWidth);
        for (int i = 0; i < copyCount; i++)
            padded[i] = values[i];

        return padded;
    }

    private static FixtureParameter[] ZeroFillHardwareParameters(IReadOnlyList<FixtureParameter> parameters)
    {
        FixtureParameter[] padded = new FixtureParameter[HardwareVectorWidth];
        int copyCount = Math.Min(parameters.Count, HardwareVectorWidth);
        for (int i = 0; i < copyCount; i++)
            padded[i] = parameters[i];

        return padded;
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

internal sealed class EvaluatedFixture(
    IReadOnlyList<ActualDiagnostic> diagnostics,
    IReadOnlyDictionary<RegressionStage, string> stageOutputs,
    string? finalAssemblyText,
    string bodyText,
    byte[]? hardwareBinary,
    MatcherTraceReport? matcherTraceReport)
{
    public IReadOnlyList<ActualDiagnostic> Diagnostics { get; } = diagnostics;
    public IReadOnlyDictionary<RegressionStage, string> StageOutputs { get; } = stageOutputs;
    public string? FinalAssemblyText { get; } = finalAssemblyText;
    public string BodyText { get; } = bodyText;
    public byte[]? HardwareBinary { get; } = hardwareBinary;
    public MatcherTraceReport? MatcherTraceReport { get; } = matcherTraceReport;

    public EvaluatedFixture WithHardwareBinary(byte[] hardwareBinary)
    {
        return new EvaluatedFixture(Diagnostics, StageOutputs, FinalAssemblyText, BodyText, hardwareBinary, MatcherTraceReport);
    }

    public EvaluatedFixture WithMatcherTrace(MatcherTraceReport matcherTraceReport)
    {
        return new EvaluatedFixture(Diagnostics, StageOutputs, FinalAssemblyText, BodyText, HardwareBinary, matcherTraceReport);
    }

    public static EvaluatedFixture ForAssembly(string bodyText)
    {
        return new EvaluatedFixture([], new Dictionary<RegressionStage, string>(), null, bodyText, null, null);
    }

    public static EvaluatedFixture Empty(string relativePath)
    {
        _ = relativePath;
        return new EvaluatedFixture([], new Dictionary<RegressionStage, string>(), null, string.Empty, null, null);
    }
}

internal sealed class CodeAssertionEvaluationResult(IReadOnlyList<string> issues, MatcherTraceReport? matcherTraceReport)
{
    public IReadOnlyList<string> Issues { get; } = issues;
    public MatcherTraceReport? MatcherTraceReport { get; } = matcherTraceReport;
}

internal enum MatcherTraceBlockKind
{
    Contains,
    Sequence,
    Exact,
}

internal sealed class MatcherTraceReport(RegressionStage? stage, IReadOnlyList<MatcherTraceBlock> blocks)
{
    public RegressionStage? Stage { get; } = stage;
    public IReadOnlyList<MatcherTraceBlock> Blocks { get; } = blocks;
}

internal sealed record class MatcherTraceBlock(
    MatcherTraceBlockKind Kind,
    int BlockNumber,
    IReadOnlyList<MatcherTraceItem> Items,
    IReadOnlyList<PatternBindingCapture> FinalBindings,
    int? FinalCursorLineIndex,
    bool Succeeded,
    string? FailureReason
    );

internal sealed record class MatcherTraceItem(
    int ItemNumber,
    SnippetItem Snippet,
    IReadOnlyList<MatcherTraceMatch> Matches,
    IReadOnlyList<PatternBindingCapture> BindingsAfterItem,
    bool Succeeded,
    string? FailureReason,
    int? SearchStartLineIndex,
    int? GapStartLineIndex,
    int? GapEndLineIndex,
    string? GapText);

internal sealed record class MatcherTraceMatch(
    int LineIndex,
    string MatchedText,
    int? SourceLineNumber,
    string? LineText,
    IReadOnlyList<PatternBindingCapture> Bindings);

internal sealed class ArtifactWriter(string repositoryRootPath, bool enabled)
{
    private const int MaxRunRoots = 10;
    private readonly string _repositoryRootPath = repositoryRootPath;
    private readonly bool _enabled = enabled;
    private string? _runRootPath;

    public string? WriteFailureArtifacts(
        RegressionFixture fixture,
        EvaluatedFixture evaluatedFixture,
        string summary,
        IReadOnlyList<string> issues,
        IReadOnlyList<HardwareRunCapture>? hardwareRuns = null)
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

            if (evaluatedFixture.MatcherTraceReport is not null)
            {
                File.WriteAllText(
                    Path.Combine(artifactDirectoryPath, "matcher-trace.txt"),
                    MatcherTraceFormatter.Format(evaluatedFixture.MatcherTraceReport));
            }
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

        if (hardwareRuns is not null)
        {
            foreach (HardwareRunCapture hardwareRun in hardwareRuns)
            {
                string dumpPath = Path.Combine(artifactDirectoryPath, $"hardware-run-{hardwareRun.RunIndex:D2}.txt");
                File.WriteAllText(dumpPath, HardwareResultDumpFormatter.Format(hardwareRun));
            }
        }

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

internal sealed class FlexspinProbeResult(bool isAvailable, string probeSummary)
{
    public bool IsAvailable { get; } = isAvailable;
    public string ProbeSummary { get; } = probeSummary;
}

internal sealed class FlexspinResult(bool succeeded, IReadOnlyList<string> outputLines)
{
    public bool Succeeded { get; } = succeeded;
    public IReadOnlyList<string> OutputLines { get; } = outputLines;
}

internal sealed class FlexspinBinaryResult(bool succeeded, IReadOnlyList<string> outputLines, byte[]? binaryBytes)
{
    public bool Succeeded { get; } = succeeded;
    public IReadOnlyList<string> OutputLines { get; } = outputLines;
    public byte[]? BinaryBytes { get; } = binaryBytes;
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
    private HardwareExecutionResult(
        HardwareExecutionKind kind,
        IReadOnlyList<string> issues,
        byte[]? binaryBytes,
        IReadOnlyList<HardwareRunCapture> completedRuns)
    {
        Kind = kind;
        Issues = issues;
        BinaryBytes = binaryBytes;
        CompletedRuns = completedRuns;
    }

    public HardwareExecutionKind Kind { get; }
    public bool Attempted => Kind != HardwareExecutionKind.NotAttempted;
    public bool IsMismatch => Kind == HardwareExecutionKind.Mismatch;
    public bool IsTechnicalError => Kind == HardwareExecutionKind.Error;
    public IReadOnlyList<string> Issues { get; }
    public byte[]? BinaryBytes { get; }
    public IReadOnlyList<HardwareRunCapture> CompletedRuns { get; }

    public static HardwareExecutionResult NotAttempted() => new(HardwareExecutionKind.NotAttempted, [], null, []);

    public static HardwareExecutionResult Succeeded(byte[] binaryBytes, IReadOnlyList<HardwareRunCapture> completedRuns) => new(HardwareExecutionKind.Succeeded, [], binaryBytes, completedRuns);

    public static HardwareExecutionResult Mismatch(IReadOnlyList<string> issues, byte[]? binaryBytes = null, IReadOnlyList<HardwareRunCapture>? completedRuns = null) => new(HardwareExecutionKind.Mismatch, issues, binaryBytes, completedRuns ?? []);

    public static HardwareExecutionResult UnexpectedSuccess(IReadOnlyList<string> issues, byte[]? binaryBytes = null, IReadOnlyList<HardwareRunCapture>? completedRuns = null) => new(HardwareExecutionKind.UnexpectedSuccess, issues, binaryBytes, completedRuns ?? []);

    public static HardwareExecutionResult Error(IReadOnlyList<string> issues, byte[]? binaryBytes = null, IReadOnlyList<HardwareRunCapture>? completedRuns = null) => new(HardwareExecutionKind.Error, issues, binaryBytes, completedRuns ?? []);
}

internal sealed class HardwareRunCapture
{
    public HardwareRunCapture(int runIndex, HardwareRunExpectation expectation, TestResult result)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runIndex);

        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentNullException.ThrowIfNull(result);

        RunIndex = runIndex;
        Expectation = expectation;
        Result = result;
    }

    public int RunIndex { get; }

    public HardwareRunExpectation Expectation { get; }

    public TestResult Result { get; }
}

internal static class MatcherTraceFormatter
{
    public static string Format(MatcherTraceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        StringBuilder builder = new();
        builder.Append("Stage: ");
        builder.AppendLine(report.Stage is RegressionStage stage ? RegressionRunner.StageName(stage) : "<none>");

        foreach (MatcherTraceBlock block in report.Blocks)
        {
            builder.AppendLine();
            builder.Append(BlockKindName(block.Kind));
            builder.Append(" block ");
            builder.Append(block.BlockNumber.ToString(CultureInfo.InvariantCulture));
            builder.Append(": ");
            builder.AppendLine(block.Succeeded ? "PASS" : "FAIL");

            if (block.FinalCursorLineIndex is int finalCursorLineIndex)
            {
                builder.Append("Final cursor line index: ");
                builder.AppendLine(finalCursorLineIndex.ToString(CultureInfo.InvariantCulture));
            }

            AppendBindings(builder, "Bindings", block.FinalBindings);

            if (!string.IsNullOrWhiteSpace(block.FailureReason))
            {
                builder.Append("Failure: ");
                builder.AppendLine(block.FailureReason);
            }

            foreach (MatcherTraceItem item in block.Items.OrderBy(static item => item.ItemNumber))
            {
                builder.AppendLine();
                builder.Append("Item ");
                builder.Append(item.ItemNumber.ToString(CultureInfo.InvariantCulture));
                builder.Append(": ");
                builder.Append(ItemKindName(item.Snippet));
                builder.Append(' ');
                builder.AppendLine(item.Succeeded ? "PASS" : "FAIL");
                builder.Append("Snippet: ");
                builder.AppendLine(item.Snippet.Pattern.Source);

                if (item.SearchStartLineIndex is int searchStartLineIndex)
                {
                    builder.Append("Search start line index: ");
                    builder.AppendLine(searchStartLineIndex.ToString(CultureInfo.InvariantCulture));
                }

                if (item.GapStartLineIndex is int gapStartLineIndex && item.GapEndLineIndex is int gapEndLineIndex)
                {
                    builder.Append("Gap lines: [");
                    builder.Append(gapStartLineIndex.ToString(CultureInfo.InvariantCulture));
                    builder.Append(", ");
                    builder.Append(gapEndLineIndex.ToString(CultureInfo.InvariantCulture));
                    builder.AppendLine(")");
                    builder.Append("Gap text: ");
                    builder.AppendLine(string.IsNullOrEmpty(item.GapText) ? "<empty>" : item.GapText);
                }

                AppendBindings(builder, "Bindings after item", item.BindingsAfterItem);

                if (!string.IsNullOrWhiteSpace(item.FailureReason))
                {
                    builder.Append("Failure: ");
                    builder.AppendLine(item.FailureReason);
                }

                if (item.Matches.Count == 0)
                {
                    builder.AppendLine("Matches: none");
                    continue;
                }

                builder.AppendLine("Matches:");
                for (int matchIndex = 0; matchIndex < item.Matches.Count; matchIndex++)
                {
                    MatcherTraceMatch match = item.Matches[matchIndex];
                    builder.Append("  [");
                    builder.Append((matchIndex + 1).ToString(CultureInfo.InvariantCulture));
                    builder.Append("] line index ");
                    builder.AppendLine(match.LineIndex.ToString(CultureInfo.InvariantCulture));
                    if (match.SourceLineNumber is int sourceLineNumber)
                    {
                        builder.Append("      source line ");
                        builder.Append(sourceLineNumber.ToString(CultureInfo.InvariantCulture));
                        builder.Append(": ");
                        builder.AppendLine(match.LineText ?? "<unknown>");
                    }
                    else
                    {
                        builder.AppendLine("      source line: <unknown>");
                    }

                    builder.Append("      matched: ");
                    builder.AppendLine(match.MatchedText);
                    AppendBindings(builder, "      matched bindings", match.Bindings);
                }
            }
        }

        return builder.ToString();
    }

    private static void AppendBindings(StringBuilder builder, string title, IReadOnlyList<PatternBindingCapture> bindings)
    {
        builder.Append(title);
        builder.Append(": ");
        if (bindings.Count == 0)
        {
            builder.AppendLine("none");
            return;
        }

        builder.AppendLine();
        foreach (PatternBindingCapture binding in bindings)
        {
            builder.Append("  ?");
            builder.Append(binding.Number.ToString(CultureInfo.InvariantCulture));
            builder.Append(" = ");
            builder.AppendLine(binding.Value);
        }
    }

    private static string BlockKindName(MatcherTraceBlockKind kind)
    {
        return kind switch
        {
            MatcherTraceBlockKind.Contains => "CONTAINS",
            MatcherTraceBlockKind.Sequence => "SEQUENCE",
            MatcherTraceBlockKind.Exact => "EXACT",
            _ => throw new UnreachableException(),
        };
    }

    private static string ItemKindName(SnippetItem item)
    {
        return item.Kind switch
        {
            SnippetKind.Positive => "positive",
            SnippetKind.Negative => "negative",
            SnippetKind.Count => $"{item.Count.ToString(CultureInfo.InvariantCulture)}x",
            _ => throw new UnreachableException(),
        };
    }
}

internal static class HardwareResultDumpFormatter
{
    public static string Format(HardwareRunCapture run)
    {
        ArgumentNullException.ThrowIfNull(run);

        StringBuilder builder = new();
        builder.Append("Run: ");
        builder.Append(run.RunIndex.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.Append("Arguments: [");
        builder.Append(string.Join(", ", run.Expectation.ParameterLiterals));
        builder.AppendLine("]");
        AppendValueList(builder, "Expected Outputs", run.Expectation.ExpectedOutputs);
        builder.AppendLine();

        AppendValueList(builder, "Inputs", run.Result.Inputs);
        builder.AppendLine();
        AppendValueList(builder, "Outputs", run.Result.Outputs);
        builder.AppendLine();
        AppendByteSection(builder, "Log", run.Result.Log);
        builder.AppendLine();
        AppendByteSection(builder, "StdOut", run.Result.StdOut);
        builder.AppendLine();
        AppendByteSection(builder, "StdErr", run.Result.StdErr);
        return builder.ToString();
    }

    private static void AppendValueList(StringBuilder builder, string title, IReadOnlyList<uint> values)
    {
        builder.AppendLine(title + ":");
        if (values.Count == 0)
        {
            builder.AppendLine("<empty>");
            return;
        }

        for (int i = 0; i < values.Count; i++)
        {
            builder.Append('[');
            builder.Append(i.ToString(CultureInfo.InvariantCulture));
            builder.Append("] ");
            builder.AppendLine(FormatValue(values[i]));
        }
    }

    private static string FormatValue(uint value)
    {
        int signedValue = unchecked((int)value);
        return string.Format(CultureInfo.InvariantCulture, "0x{0:X8} | unsigned {1} | signed {2}", value, value, signedValue);
    }

    private static void AppendByteSection(StringBuilder builder, string title, ReadOnlySequence<byte> bytes)
    {
        builder.AppendLine(title + ":");
        AppendEscapedBytes(builder, bytes);
    }

    private static void AppendEscapedBytes(StringBuilder builder, ReadOnlySequence<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            builder.AppendLine("<empty>");
            return;
        }

        bool endedWithLf = false;
        foreach (ReadOnlyMemory<byte> segment in bytes)
        {
            foreach (byte value in segment.Span)
            {
                endedWithLf = false;
                if (value == (byte)'\n')
                {
                    builder.Append("<LF>");
                    builder.AppendLine();
                    endedWithLf = true;
                    continue;
                }

                string? controlCodeName = TryGetControlCodeName(value);
                if (controlCodeName is not null)
                {
                    builder.Append('<');
                    builder.Append(controlCodeName);
                    builder.Append('>');
                    continue;
                }

                if (value >= 0x20 && value <= 0x7E)
                {
                    builder.Append((char)value);
                    continue;
                }

                builder.Append("<0x");
                builder.Append(value.ToString("X2", CultureInfo.InvariantCulture));
                builder.Append('>');
            }
        }

        if (!endedWithLf)
            builder.AppendLine();
    }

    private static string? TryGetControlCodeName(byte value)
    {
        return value switch
        {
            0x00 => "NUL",
            0x01 => "SOH",
            0x02 => "STX",
            0x03 => "ETX",
            0x04 => "EOT",
            0x05 => "ENQ",
            0x06 => "ACK",
            0x07 => "BEL",
            0x08 => "BS",
            0x09 => "TAB",
            0x0B => "VT",
            0x0C => "FF",
            0x0D => "CR",
            0x0E => "SO",
            0x0F => "SI",
            0x10 => "DLE",
            0x11 => "DC1",
            0x12 => "DC2",
            0x13 => "DC3",
            0x14 => "DC4",
            0x15 => "NAK",
            0x16 => "SYN",
            0x17 => "ETB",
            0x18 => "CAN",
            0x19 => "EM",
            0x1A => "SUB",
            0x1B => "ESC",
            0x1C => "FS",
            0x1D => "GS",
            0x1E => "RS",
            0x1F => "US",
            0x7F => "DEL",
            _ => null,
        };
    }
}

internal static class HardwareFixtureRunner
{
    public static TestResult Run(
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
