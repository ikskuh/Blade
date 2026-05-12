using System.Collections.Generic;
using Blade.Diagnostics;
using Blade.HwTestRunner;

namespace Blade.Regressions;

/// <summary>Identifies the outcome assigned to a regression fixture execution.</summary>
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

/// <summary>Identifies the file-level fixture format used by a regression sample.</summary>
public enum RegressionFixtureKind
{
    Blade,
    BladeCrash,
}

/// <summary>Describes the compile or execution contract encoded in a regression header.</summary>
public enum RegressionExpectationKind
{
    Pass,
    PassHw,
    Fail,
    XFail,
    XPass,
    XFailHw,
}

/// <summary>Identifies the compiler stage that snippet assertions target.</summary>
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

/// <summary>Describes whether FlexSpin validation is required for a fixture.</summary>
public enum FlexspinExpectation
{
    Auto,
    Required,
    Forbidden,
}

/// <summary>Classifies a snippet assertion item within a matcher block.</summary>
public enum SnippetKind
{
    Positive,
    Negative,
    Count,
}

/// <summary>Represents a parsed regression fixture together with its expected behavior.</summary>
public sealed class RegressionFixture(
    string absolutePath,
    string relativePath,
    RegressionFixtureKind kind,
    string text,
    string bodyText,
    RegressionExpectation expectation)
{
    /// <summary>Gets the absolute file path for the fixture on disk.</summary>
    public string AbsolutePath { get; } = absolutePath;

    /// <summary>Gets the repository-relative fixture path used in reports.</summary>
    public string RelativePath { get; } = relativePath;

    /// <summary>Gets the fixture file kind inferred from the filename.</summary>
    public RegressionFixtureKind Kind { get; } = kind;

    /// <summary>Gets the full original text of the fixture file.</summary>
    public string Text { get; } = text;

    /// <summary>Gets the fixture body text after any encoded header has been removed.</summary>
    public string BodyText { get; } = bodyText;

    /// <summary>Gets the parsed expectations that govern fixture evaluation.</summary>
    public RegressionExpectation Expectation { get; } = expectation;
}

/// <summary>Represents one matcher item inside a snippet assertion block.</summary>
public sealed class SnippetItem
{
    private SnippetItem(SnippetKind kind, Pattern pattern, int count)
    {
        this.Kind = kind;
        this.Pattern = pattern;
        this.Count = count;
    }

    /// <summary>Gets the matcher behavior for this snippet item.</summary>
    public SnippetKind Kind { get; }

    /// <summary>Gets the raw snippet text to normalize and match.</summary>
    public Pattern Pattern { get; }

    /// <summary>Gets the exact occurrence count required for count-based items.</summary>
    public int Count { get; }

    /// <summary>Creates a positive snippet assertion item.</summary>
    public static SnippetItem Positive(Pattern pattern) => new(SnippetKind.Positive, pattern, 0);

    /// <summary>Creates a negative snippet assertion item.</summary>
    public static SnippetItem Negative(Pattern pattern) => new(SnippetKind.Negative, pattern, 0);

    /// <summary>Creates a snippet assertion item that requires an exact match count.</summary>
    public static SnippetItem ExactCount(Pattern pattern, int count)
    {
        return new SnippetItem(SnippetKind.Count, pattern, count);
    }
}

/// <summary>Represents an ordered snippet assertion block from a regression header.</summary>
public sealed class SnippetBlock(IReadOnlyList<SnippetItem> items)
{
    /// <summary>Gets the snippet items that belong to this block.</summary>
    public IReadOnlyList<SnippetItem> Items { get; } = items;
}

/// <summary>Represents one expected hardware execution for a fixture.</summary>
public sealed class HardwareRunExpectation(
    IReadOnlyList<FixtureParameter> parameters,
    IReadOnlyList<string> parameterLiterals,
    IReadOnlyList<uint> expectedOutputs)
{
    /// <summary>Gets the padded hardware parameter vector for the run.</summary>
    public IReadOnlyList<FixtureParameter> Parameters { get; } = parameters;

    /// <summary>Gets the original parameter literals used for diagnostics and dumps.</summary>
    public IReadOnlyList<string> ParameterLiterals { get; } = parameterLiterals;

    /// <summary>Gets the padded expected hardware output vector for the run.</summary>
    public IReadOnlyList<uint> ExpectedOutputs { get; } = expectedOutputs;
}

/// <summary>Collects all parsed expectations declared by a regression fixture.</summary>
public sealed record class RegressionExpectation(
    RegressionExpectationKind ExpectationKind,
    RegressionStage? Stage,
    IReadOnlyList<SnippetBlock> ContainsBlocks,
    IReadOnlyList<SnippetBlock> SequenceBlocks,
    IReadOnlyList<SnippetBlock> ExactBlocks,
    IReadOnlyList<string> LooseDiagnosticNames,
    IReadOnlyList<ExpectedDiagnostic> ExactDiagnostics,
    FlexspinExpectation FlexspinExpectation,
    IReadOnlyList<string> CompilerArgs,
    IReadOnlyList<HardwareRunExpectation> HardwareRuns)
{
    /// <summary>Gets whether the expectation includes any snippet-based code assertions.</summary>
    public bool HasCodeAssertions => ContainsBlocks.Count > 0 || SequenceBlocks.Count > 0 || ExactBlocks.Count > 0;

    /// <summary>Gets whether the expectation includes any diagnostic assertions.</summary>
    public bool HasDiagnosticAssertions => LooseDiagnosticNames.Count > 0 || ExactDiagnostics.Count > 0;
}

/// <summary>Represents one expected diagnostic emitted by a fixture.</summary>
public sealed record class ExpectedDiagnostic(string Name, int? Line, string? Message)
{
    /// <summary>Formats the diagnostic expectation for human-readable reporting.</summary>
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

/// <summary>Represents one actual diagnostic produced while evaluating a fixture.</summary>
public sealed record class ActualDiagnostic(string Name, DiagnosticSeverity Severity, int Line, string Message)
{
    /// <summary>Formats the diagnostic for failure output and artifact dumps.</summary>
    public string Display() => $"L{Line}, {Name}: {Message}";
}
