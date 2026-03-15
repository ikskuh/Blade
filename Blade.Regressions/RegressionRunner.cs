using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Blade;
using Blade.Diagnostics;
using Blade.IR;
using Blade.Source;

namespace Blade.Regressions;

public sealed class RegressionRunOptions
{
    public string? RepositoryRootPath { get; init; }
    public IReadOnlyList<string> Filters { get; init; } = [];
    public bool WriteFailureArtifacts { get; init; } = true;
}

public sealed class RegressionRunResult
{
    public RegressionRunResult(string repositoryRootPath, IReadOnlyList<RegressionFixtureResult> fixtureResults)
    {
        RepositoryRootPath = repositoryRootPath;
        FixtureResults = fixtureResults;
    }

    public string RepositoryRootPath { get; }
    public IReadOnlyList<RegressionFixtureResult> FixtureResults { get; }
    public int PassCount => FixtureResults.Count(result => result.Outcome == RegressionFixtureOutcome.Pass);
    public int FailCount => FixtureResults.Count(result => result.Outcome == RegressionFixtureOutcome.Fail);
    public int XFailCount => FixtureResults.Count(result => result.Outcome == RegressionFixtureOutcome.XFail);
    public int UnexpectedPassCount => FixtureResults.Count(result => result.Outcome == RegressionFixtureOutcome.UnexpectedPass);
    public int SkipCount => FixtureResults.Count(result => result.Outcome == RegressionFixtureOutcome.Skipped);
    public bool Succeeded => FailCount == 0 && UnexpectedPassCount == 0;
}

public sealed class RegressionFixtureResult
{
    public RegressionFixtureResult(
        string relativePath,
        RegressionFixtureOutcome outcome,
        string summary,
        IReadOnlyList<string> details,
        string? artifactDirectoryPath)
    {
        RelativePath = relativePath;
        Outcome = outcome;
        Summary = summary;
        Details = details;
        ArtifactDirectoryPath = artifactDirectoryPath;
    }

    public string RelativePath { get; }
    public RegressionFixtureOutcome Outcome { get; }
    public string Summary { get; }
    public IReadOnlyList<string> Details { get; }
    public string? ArtifactDirectoryPath { get; }
}

public enum RegressionFixtureOutcome
{
    Pass,
    Fail,
    XFail,
    UnexpectedPass,
    Skipped,
}

public enum RegressionFixtureKind
{
    Blade,
    Spin2,
    Pasm2,
}

public enum RegressionExpectationKind
{
    Pass,
    Fail,
    XFail,
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

public sealed class RegressionExpectation
{
    public RegressionExpectation(
        RegressionExpectationKind expectationKind,
        RegressionStage? stage,
        IReadOnlyList<string> containsSnippets,
        IReadOnlyList<string> notContainsSnippets,
        IReadOnlyList<string> sequenceSnippets,
        string? exactText,
        IReadOnlyList<string> looseDiagnosticCodes,
        IReadOnlyList<ExpectedDiagnostic> exactDiagnostics,
        FlexspinExpectation flexspinExpectation,
        IReadOnlyList<string> compilerArgs)
    {
        ExpectationKind = expectationKind;
        Stage = stage;
        ContainsSnippets = containsSnippets;
        NotContainsSnippets = notContainsSnippets;
        SequenceSnippets = sequenceSnippets;
        ExactText = exactText;
        LooseDiagnosticCodes = looseDiagnosticCodes;
        ExactDiagnostics = exactDiagnostics;
        FlexspinExpectation = flexspinExpectation;
        CompilerArgs = compilerArgs;
    }

    public RegressionExpectationKind ExpectationKind { get; }
    public RegressionStage? Stage { get; }
    public IReadOnlyList<string> ContainsSnippets { get; }
    public IReadOnlyList<string> NotContainsSnippets { get; }
    public IReadOnlyList<string> SequenceSnippets { get; }
    public string? ExactText { get; }
    public IReadOnlyList<string> LooseDiagnosticCodes { get; }
    public IReadOnlyList<ExpectedDiagnostic> ExactDiagnostics { get; }
    public FlexspinExpectation FlexspinExpectation { get; }
    public IReadOnlyList<string> CompilerArgs { get; }
    public bool HasCodeAssertions => ContainsSnippets.Count > 0 || NotContainsSnippets.Count > 0 || SequenceSnippets.Count > 0 || ExactText is not null;
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
        string repositoryRootPath = RepositoryLayout.FindRepositoryRoot(effectiveOptions.RepositoryRootPath);

        FlexspinProbeResult flexspinProbe = FlexspinRunner.ProbeAvailability();
        List<string> fixturePaths = DiscoverFixturePaths(repositoryRootPath, effectiveOptions.Filters);
        List<RegressionFixtureResult> fixtureResults = [];
        ArtifactWriter artifactWriter = new(repositoryRootPath, effectiveOptions.WriteFailureArtifacts);

        foreach (string fixturePath in fixturePaths)
        {
            RegressionFixtureResult result = EvaluateFixture(repositoryRootPath, fixturePath, artifactWriter, flexspinProbe);
            fixtureResults.Add(result);
        }

        return new RegressionRunResult(repositoryRootPath, fixtureResults);
    }

    private static List<string> DiscoverFixturePaths(string repositoryRootPath, IReadOnlyList<string> filters)
    {
        List<string> paths = [];
        AddFixturePaths(paths, Path.Combine(repositoryRootPath, "Examples"), "*.blade");
        AddFixturePaths(paths, Path.Combine(repositoryRootPath, "Demonstrators"), "*.blade");
        AddFixturePaths(paths, Path.Combine(repositoryRootPath, "RegressionTests"), "*.blade");
        AddFixturePaths(paths, Path.Combine(repositoryRootPath, "RegressionTests"), "*.spin2");
        AddFixturePaths(paths, Path.Combine(repositoryRootPath, "RegressionTests"), "*.pasm2");

        IEnumerable<string> filteredPaths = paths;
        if (filters.Count > 0)
        {
            filteredPaths = filteredPaths.Where(path =>
            {
                string relativePath = Path.GetRelativePath(repositoryRootPath, path).Replace('\\', '/');
                return filters.Any(filter => relativePath.Contains(filter, StringComparison.OrdinalIgnoreCase));
            });
        }

        return filteredPaths.OrderBy(path => path, StringComparer.Ordinal).ToList();
    }

    private static void AddFixturePaths(List<string> paths, string directoryPath, string searchPattern)
    {
        if (!Directory.Exists(directoryPath))
            return;

        string[] discovered = Directory.GetFiles(directoryPath, searchPattern, SearchOption.AllDirectories);
        paths.AddRange(discovered);
    }

    private static RegressionFixtureResult EvaluateFixture(string repositoryRootPath, string fixturePath, ArtifactWriter artifactWriter, FlexspinProbeResult flexspinProbe)
    {
        string relativePath = Path.GetRelativePath(repositoryRootPath, fixturePath).Replace('\\', '/');

        try
        {
            RegressionFixture fixture = RegressionFixtureParser.Parse(repositoryRootPath, fixturePath);
            EvaluatedFixture evaluatedFixture = ExecuteFixture(fixture);
            List<string> issues = [];

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

            return new RegressionFixtureResult(relativePath, outcome, summary, issues, artifactDirectoryPath);
        }
        catch (Exception ex)
        {
            List<string> details =
            [
                $"Unhandled regression runner error: {ex.Message}",
            ];
            EvaluatedFixture failedFixture = EvaluatedFixture.Empty(relativePath);
            RegressionFixture syntheticFixture = new(
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
                    null,
                    [],
                    [],
                    FlexspinExpectation.Forbidden,
                    []));
            string summary = "fixture evaluation crashed";
            string? artifactDirectoryPath = artifactWriter.WriteFailureArtifacts(syntheticFixture, failedFixture, summary, details);
            return new RegressionFixtureResult(relativePath, RegressionFixtureOutcome.Fail, summary, details, artifactDirectoryPath);
        }
    }

    private static EvaluatedFixture ExecuteFixture(RegressionFixture fixture)
    {
        return fixture.Kind switch
        {
            RegressionFixtureKind.Blade => ExecuteBladeFixture(fixture),
            RegressionFixtureKind.Pasm2 => EvaluatedFixture.ForAssembly(fixture.BodyText),
            RegressionFixtureKind.Spin2 => EvaluatedFixture.ForAssembly(fixture.BodyText),
            _ => throw new InvalidOperationException($"Unknown fixture kind '{fixture.Kind}'."),
        };
    }

    private static EvaluatedFixture ExecuteBladeFixture(RegressionFixture fixture)
    {
        CompilationOptions options = BuildCompilationOptions(fixture.Expectation.CompilerArgs);
        CompilationResult compilation = CompilerDriver.Compile(fixture.Text, fixture.AbsolutePath, options);
        List<ActualDiagnostic> diagnostics = compilation.Diagnostics
            .Select(diag =>
            {
                SourceLocation location = compilation.Source.GetLocation(diag.Span.Start);
                return new ActualDiagnostic(diag.FormatCode(), location.Line, diag.Message);
            })
            .ToList();

        Dictionary<RegressionStage, string> stageOutputs = [];
        if (compilation.IrBuildResult is not null)
        {
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
            fixture.BodyText);
    }

    private static CompilationOptions BuildCompilationOptions(IReadOnlyList<string> compilerArgs)
    {
        bool enableSingleCallsiteInlining = true;
        List<OptimizationDirective> directives = [];

        foreach (string arg in compilerArgs)
        {
            if (TryParseOptimizationDirective(arg, OptimizationStage.Mir, "-fmir-opt=", enable: true, out OptimizationDirective? directive)
                || TryParseOptimizationDirective(arg, OptimizationStage.Mir, "-fno-mir-opt=", enable: false, out directive)
                || TryParseOptimizationDirective(arg, OptimizationStage.Lir, "-flir-opt=", enable: true, out directive)
                || TryParseOptimizationDirective(arg, OptimizationStage.Lir, "-fno-lir-opt=", enable: false, out directive)
                || TryParseOptimizationDirective(arg, OptimizationStage.Asmir, "-fasmir-opt=", enable: true, out directive)
                || TryParseOptimizationDirective(arg, OptimizationStage.Asmir, "-fno-asmir-opt=", enable: false, out directive))
            {
                directives.Add(directive!.Value);
                continue;
            }

            throw new InvalidOperationException($"Unsupported ARGS option '{arg}'.");
        }

        return new CompilationOptions
        {
            EnableSingleCallsiteInlining = enableSingleCallsiteInlining,
            OptimizationDirectives = directives,
        };
    }

    private static bool TryParseOptimizationDirective(
        string arg,
        OptimizationStage stage,
        string prefix,
        bool enable,
        out OptimizationDirective? directive)
    {
        directive = null;
        if (!arg.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        string csv = arg[prefix.Length..];
        string[] rawNames = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rawNames.Length == 0)
            throw new InvalidOperationException($"Missing optimization list in ARGS option '{arg}'.");

        List<string> names = [];
        foreach (string name in rawNames)
        {
            if (name == "*")
            {
                names.Add(name);
                continue;
            }

            if (!OptimizationCatalog.IsKnown(stage, name))
                throw new InvalidOperationException($"Unknown {stage.ToString().ToLowerInvariant()} optimization '{name}' in ARGS option.");
            names.Add(name);
        }

        directive = new OptimizationDirective(stage, enable, names);
        return true;
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

        if (expectation.ExpectationKind == RegressionExpectationKind.Pass && diagnostics.Count > 0)
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

        if (fixture.Kind == RegressionFixtureKind.Blade)
        {
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

        string normalizedAssembly = CodeNormalizer.NormalizeAssemblyText(evaluatedFixture.BodyText);
        issues.AddRange(EvaluateNormalizedAssertions(expectation, normalizedAssembly, null));
        return issues;
    }

    private static List<string> EvaluateNormalizedAssertions(
        RegressionExpectation expectation,
        string normalizedActual,
        RegressionStage? stage)
    {
        List<string> issues = [];

        foreach (string snippet in expectation.ContainsSnippets)
        {
            string normalizedSnippet = NormalizeExpectedCode(snippet, stage);
            if (!normalizedActual.Contains(normalizedSnippet, StringComparison.Ordinal))
                issues.Add($"missing snippet: {snippet}");
        }

        foreach (string snippet in expectation.NotContainsSnippets)
        {
            string normalizedSnippet = NormalizeExpectedCode(snippet, stage);
            if (normalizedActual.Contains(normalizedSnippet, StringComparison.Ordinal))
                issues.Add($"unexpected snippet present: {snippet}");
        }

        if (expectation.SequenceSnippets.Count > 0)
        {
            int index = 0;
            foreach (string snippet in expectation.SequenceSnippets)
            {
                string normalizedSnippet = NormalizeExpectedCode(snippet, stage);
                int foundIndex = normalizedActual.IndexOf(normalizedSnippet, index, StringComparison.Ordinal);
                if (foundIndex < 0)
                {
                    issues.Add($"missing ordered snippet: {snippet}");
                    break;
                }

                index = foundIndex + normalizedSnippet.Length;
            }
        }

        if (expectation.ExactText is not null)
        {
            string normalizedExpected = NormalizeExpectedCode(expectation.ExactText, stage);
            if (!string.Equals(normalizedActual, normalizedExpected, StringComparison.Ordinal))
                issues.Add($"exact text mismatch: {NormalizedDiffBuilder.Build(normalizedExpected, normalizedActual)}");
        }

        return issues;
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

        string? sourceText = fixture.Kind switch
        {
            RegressionFixtureKind.Blade => evaluatedFixture.FinalAssemblyText,
            RegressionFixtureKind.Pasm2 => PasmWrapper.Wrap(fixture.BodyText),
            RegressionFixtureKind.Spin2 => fixture.BodyText,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(sourceText))
        {
            issues.Add("FlexSpin validation was required, but no assembly text was available");
            return issues;
        }

        string fileExtension = fixture.Kind switch
        {
            RegressionFixtureKind.Blade => ".spin2",
            RegressionFixtureKind.Pasm2 => ".spin2",
            RegressionFixtureKind.Spin2 => ".spin2",
            _ => ".spin2",
        };
        FlexspinResult result = FlexspinRunner.Run(sourceText, fileExtension);
        if (!result.Succeeded)
        {
            issues.Add("FlexSpin failed:");
            issues.AddRange(result.OutputLines);
        }

        return issues;
    }

    private static bool ShouldRunFlexspin(RegressionFixture fixture)
    {
        return fixture.Expectation.FlexspinExpectation switch
        {
            FlexspinExpectation.Required => true,
            FlexspinExpectation.Forbidden => false,
            FlexspinExpectation.Auto => fixture.Kind != RegressionFixtureKind.Blade
                || fixture.Expectation.ExpectationKind == RegressionExpectationKind.Pass,
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
            if (matched && LooksLikeUnexpectedPass(fixture, evaluatedFixture))
                return RegressionFixtureOutcome.UnexpectedPass;

            return RegressionFixtureOutcome.XFail;
        }

        return matched ? RegressionFixtureOutcome.Pass : RegressionFixtureOutcome.Fail;
    }

    private static bool LooksLikeUnexpectedPass(RegressionFixture fixture, EvaluatedFixture evaluatedFixture)
    {
        if (fixture.Kind != RegressionFixtureKind.Blade)
            return true;

        return evaluatedFixture.Diagnostics.Count == 0 && evaluatedFixture.FinalAssemblyText is not null;
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
        string bodyText)
    {
        Diagnostics = diagnostics;
        StageOutputs = stageOutputs;
        FinalAssemblyText = finalAssemblyText;
        BodyText = bodyText;
    }

    public IReadOnlyList<ActualDiagnostic> Diagnostics { get; }
    public IReadOnlyDictionary<RegressionStage, string> StageOutputs { get; }
    public string? FinalAssemblyText { get; }
    public string BodyText { get; }

    public static EvaluatedFixture ForAssembly(string bodyText)
    {
        return new EvaluatedFixture([], new Dictionary<RegressionStage, string>(), null, bodyText);
    }

    public static EvaluatedFixture Empty(string relativePath)
    {
        _ = relativePath;
        return new EvaluatedFixture([], new Dictionary<RegressionStage, string>(), null, string.Empty);
    }
}

internal static class RegressionFixtureParser
{
    private static readonly Regex DirectiveRegex = new(
        @"^(?<name>EXPECT|NOTE|DIAGNOSTICS|STAGE|CONTAINS|NOT_CONTAINS|SEQUENCE|EXACT|FLEXSPIN|ARGS):(?<value>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ExactDiagnosticRegex = new(
        @"^(?:L(?<line>\d+)\s*,\s*)?(?<code>[EWI]\d{4})(?:\s*:\s*(?<message>.+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static RegressionFixture Parse(string repositoryRootPath, string fixturePath)
    {
        string text = File.ReadAllText(fixturePath);
        string relativePath = Path.GetRelativePath(repositoryRootPath, fixturePath).Replace('\\', '/');
        RegressionFixtureKind kind = DetermineFixtureKind(fixturePath);
        HeaderScanResult headerScan = HeaderScanResult.Scan(text, kind);

        if (relativePath.StartsWith("Examples/", StringComparison.Ordinal) && headerScan.HasDirectiveHeader)
            throw new InvalidOperationException("Examples fixtures must not contain expectation headers.");

        RegressionExpectation expectation = headerScan.HasDirectiveHeader
            ? ParseExpectation(headerScan, kind)
            : new RegressionExpectation(
                RegressionExpectationKind.Pass,
                null,
                [],
                [],
                [],
                null,
                [],
                [],
                FlexspinExpectation.Auto,
                []);

        if (kind != RegressionFixtureKind.Blade && expectation.HasDiagnosticAssertions)
            throw new InvalidOperationException("Assembly fixtures do not support DIAGNOSTICS assertions.");

        if (kind == RegressionFixtureKind.Blade && expectation.HasCodeAssertions && expectation.Stage is null)
            throw new InvalidOperationException("Blade fixtures with code assertions must specify STAGE.");

        if (kind != RegressionFixtureKind.Blade && expectation.Stage is not null)
            throw new InvalidOperationException("STAGE is only valid for .blade fixtures.");

        if (kind != RegressionFixtureKind.Blade && expectation.CompilerArgs.Count > 0)
            throw new InvalidOperationException("ARGS is only valid for .blade fixtures.");

        if (expectation.ExpectationKind == RegressionExpectationKind.Pass
            && EnumerateExpectedDiagnosticCodes(expectation).Any(code => code.StartsWith('E')))
        {
            throw new InvalidOperationException("EXPECT: pass cannot be combined with error diagnostic expectations.");
        }

        return new RegressionFixture(fixturePath, relativePath, kind, text, headerScan.BodyText, expectation);
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
        string extension = Path.GetExtension(fixturePath);
        return extension switch
        {
            ".blade" => RegressionFixtureKind.Blade,
            ".spin2" => RegressionFixtureKind.Spin2,
            ".pasm2" => RegressionFixtureKind.Pasm2,
            _ => throw new InvalidOperationException($"Unsupported regression fixture extension '{extension}'."),
        };
    }

    private static RegressionExpectation ParseExpectation(HeaderScanResult headerScan, RegressionFixtureKind kind)
    {
        RegressionExpectationKind expectationKind = RegressionExpectationKind.Pass;
        RegressionStage? stage = null;
        List<string> containsSnippets = [];
        List<string> notContainsSnippets = [];
        List<string> sequenceSnippets = [];
        List<string> looseDiagnosticCodes = [];
        List<ExpectedDiagnostic> exactDiagnostics = [];
        FlexspinExpectation flexspinExpectation = FlexspinExpectation.Auto;
        List<string> compilerArgs = [];
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
                    "NOT_CONTAINS" => HeaderBlock.NotContains,
                    "SEQUENCE" => HeaderBlock.Sequence,
                    "EXACT" => HeaderBlock.Exact,
                    "ARGS" => HeaderBlock.Args,
                    _ => null,
                };

                switch (directiveName)
                {
                    case "EXPECT":
                        expectationKind = directiveValue switch
                        {
                            "pass" => RegressionExpectationKind.Pass,
                            "fail" => RegressionExpectationKind.Fail,
                            "xfail" => RegressionExpectationKind.XFail,
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

                    case "NOT_CONTAINS":
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
                continue;

            switch (activeBlock.Value)
            {
                case HeaderBlock.Note:
                    break;

                case HeaderBlock.Contains:
                    containsSnippets.Add(ParseBulletItem(trimmed, "CONTAINS"));
                    break;

                case HeaderBlock.NotContains:
                    notContainsSnippets.Add(ParseBulletItem(trimmed, "NOT_CONTAINS"));
                    break;

                case HeaderBlock.Sequence:
                    sequenceSnippets.Add(ParseBulletItem(trimmed, "SEQUENCE"));
                    break;

                case HeaderBlock.Exact:
                    if (exactText is null)
                        throw new InvalidOperationException("EXACT block started without a buffer.");
                    exactText.AppendLine(line.Content);
                    break;

                case HeaderBlock.Args:
                    compilerArgs.Add(ParseBulletItem(trimmed, "ARGS"));
                    break;

                case HeaderBlock.ExactDiagnostics:
                    exactDiagnostics.Add(ParseExactDiagnostic(ParseBulletItem(trimmed, "DIAGNOSTICS")));
                    break;

                default:
                    throw new InvalidOperationException($"Unknown header block '{activeBlock.Value}'.");
            }
        }

        if (kind != RegressionFixtureKind.Blade)
            stage = null;

        string? exact = exactText?.ToString().TrimEnd();
        return new RegressionExpectation(
            expectationKind,
            stage,
            containsSnippets,
            notContainsSnippets,
            sequenceSnippets,
            exact,
            looseDiagnosticCodes,
            exactDiagnostics,
            flexspinExpectation,
            compilerArgs);
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

    private static string ParseBulletItem(string trimmed, string directiveName)
    {
        if (!trimmed.StartsWith('-'))
            throw new InvalidOperationException($"{directiveName} block entries must begin with '-'.");
        return trimmed[1..].TrimStart();
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

    private enum HeaderBlock
    {
        Note,
        ExactDiagnostics,
        Contains,
        NotContains,
        Sequence,
        Exact,
        Args,
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

        public static HeaderScanResult Scan(string text, RegressionFixtureKind kind)
        {
            string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            List<HeaderLine> headerLines = [];
            int bodyStartIndex = 0;

            while (bodyStartIndex < lines.Length)
            {
                string line = lines[bodyStartIndex];
                if (string.IsNullOrWhiteSpace(line))
                {
                    headerLines.Add(new HeaderLine(false, string.Empty));
                    bodyStartIndex++;
                    continue;
                }

                if (TryStripCommentPrefix(line, kind, out string? content))
                {
                    headerLines.Add(new HeaderLine(true, content));
                    bodyStartIndex++;
                    continue;
                }

                break;
            }

            bool hasDirectiveHeader = headerLines.Any(line =>
                line.IsComment
                && DirectiveRegex.IsMatch(line.Content.TrimStart()));

            string bodyText = hasDirectiveHeader
                ? string.Join('\n', lines.Skip(bodyStartIndex))
                : text;
            return new HeaderScanResult(headerLines, bodyText, hasDirectiveHeader);
        }

        private static bool TryStripCommentPrefix(string line, RegressionFixtureKind kind, out string content)
        {
            if (kind == RegressionFixtureKind.Blade)
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
            }
            else
            {
                string trimmedStart = line.TrimStart();
                if (trimmedStart.StartsWith('\'')
                    || trimmedStart.StartsWith(';'))
                {
                    int prefixIndex = line.IndexOf(trimmedStart[0], StringComparison.Ordinal);
                    content = line[(prefixIndex + 1)..];
                    if (content.StartsWith(' '))
                        content = content[1..];
                    return true;
                }
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

internal static class RepositoryLayout
{
    public static string FindRepositoryRoot(string? explicitRootPath)
    {
        if (explicitRootPath is not null)
            return Path.GetFullPath(explicitRootPath);

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
                if (LooksLikeRepositoryRoot(current))
                    return current;
                DirectoryInfo? parent = Directory.GetParent(current);
                current = parent?.FullName;
            }
        }

        throw new InvalidOperationException("Unable to locate the Blade repository root.");
    }

    private static bool LooksLikeRepositoryRoot(string path)
    {
        return File.Exists(Path.Combine(path, "justfile"))
            && Directory.Exists(Path.Combine(path, "Blade"))
            && Directory.Exists(Path.Combine(path, "Examples"))
            && Directory.Exists(Path.Combine(path, "Blade.Tests"));
    }
}

internal static class PasmWrapper
{
    public static string Wrap(string bodyText)
    {
        return $"DAT{Environment.NewLine}    org 0{Environment.NewLine}{bodyText}";
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

    public static FlexspinResult Run(string sourceText, string fileExtension)
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "blade-regressions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string sourcePath = Path.Combine(tempDirectoryPath, $"fixture{fileExtension}");
        File.WriteAllText(sourcePath, sourceText);

        ProcessStartInfo startInfo = new()
        {
            FileName = "flexspin",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-2");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("-q");
        startInfo.ArgumentList.Add(sourcePath);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start flexspin.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        List<string> outputLines = stdout
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();

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

        return new FlexspinResult(process.ExitCode == 0, outputLines);
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
            builder.Append(fixtureResult.Outcome.ToString().ToUpperInvariant().PadRight(14));
            builder.Append(' ');
            builder.AppendLine(fixtureResult.RelativePath);
            builder.Append("  ");
            builder.AppendLine(fixtureResult.Summary);
            foreach (string detail in fixtureResult.Details)
            {
                builder.Append("  ");
                builder.AppendLine(detail);
            }
            if (fixtureResult.ArtifactDirectoryPath is not null)
            {
                builder.Append("  artifacts: ");
                builder.AppendLine(fixtureResult.ArtifactDirectoryPath);
            }
        }

        builder.AppendLine();
        builder.Append("Repository: ");
        builder.AppendLine(result.RepositoryRootPath);
        builder.Append("Fixtures  : ");
        builder.AppendLine(result.FixtureResults.Count.ToString(CultureInfo.InvariantCulture));
        builder.Append("Pass      : ");
        builder.AppendLine(result.PassCount.ToString(CultureInfo.InvariantCulture));
        builder.Append("XFail     : ");
        builder.AppendLine(result.XFailCount.ToString(CultureInfo.InvariantCulture));
        builder.Append("Fail      : ");
        builder.AppendLine(result.FailCount.ToString(CultureInfo.InvariantCulture));
        builder.Append("Unexpected: ");
        builder.AppendLine(result.UnexpectedPassCount.ToString(CultureInfo.InvariantCulture));
        builder.Append("Skipped   : ");
        builder.AppendLine(result.SkipCount.ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}

internal static class RegressionCommandLine
{
    public static RegressionRunOptions Parse(string[] args)
    {
        string? repositoryRootPath = null;
        bool writeFailureArtifacts = true;
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

                case "--no-artifacts":
                    writeFailureArtifacts = false;
                    break;

                default:
                    filters.Add(arg);
                    break;
            }
        }

        return new RegressionRunOptions
        {
            RepositoryRootPath = repositoryRootPath,
            Filters = filters,
            WriteFailureArtifacts = writeFailureArtifacts,
        };
    }
}
