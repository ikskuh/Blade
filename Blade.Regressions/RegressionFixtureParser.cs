using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Blade.Diagnostics;
using Blade.HwTestRunner;

namespace Blade.Regressions;

/// <summary>Parses regression fixtures and any encoded header expectations they contain.</summary>
internal static class RegressionFixtureParser
{
    private const int HardwareVectorWidth = 8;

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



    /// <summary>Parses a discovered regression fixture into its executable model.</summary>
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

    /// <summary>Parses an encoded regression expectation from raw fixture text without loading a file from disk.</summary>
    public static RegressionExpectation ParseEncodedExpectation(string fixtureText)
    {
        ArgumentNullException.ThrowIfNull(fixtureText);

        HeaderScanResult headerScan = HeaderScanResult.Scan(fixtureText);
        RegressionExpectation expectation = headerScan.HasDirectiveHeader
            ? ParseExpectation(headerScan)
            : CreateDefaultExpectation(RegressionExpectationKind.Pass);

        ValidateExpectation(expectation, requireFailDiagnosticAssertions: headerScan.HasDirectiveHeader);
        return expectation;
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
                            "asmir-prealloc" => RegressionStage.AsmirPreRegisterAllocation,
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

    private static string ParseBulletItem(string trimmed, string directiveName)
    {
        if (!trimmed.StartsWith('-'))
            throw new InvalidOperationException($"{directiveName} block entries must begin with '-'.");
        return trimmed[1..].TrimStart();
    }

    private static readonly Regex PatternRegex = new(@"^(?<marker>\!|-|(?<count>\d+)x)\s+(?<text>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static SnippetItem ParseSnippetItem(string trimmed, string directiveName)
    {
        Match match = PatternRegex.Match(trimmed);
        if (!match.Success)
            throw new InvalidOperationException($"{directiveName} block entries must begin with '-', '!', or a count prefix (e.g. '3x').");


        var snippet = match.Groups["text"].Value;
        if (string.IsNullOrWhiteSpace(snippet))
            throw new InvalidOperationException($"{directiveName} block entries must include snippet text.");

        var pattern = Pattern.Compile(snippet);

        var marker = match.Groups["marker"].Value;
        switch (marker)
        {
            case "-": return SnippetItem.Positive(pattern);
            case "!": return SnippetItem.Negative(pattern);
            default:
                int count = int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture);
                if (count == 0)
                    throw new InvalidOperationException($"{directiveName} count prefixes must be greater than zero. Use '!' for negative assertions.");
                return SnippetItem.ExactCount(pattern, count);
        }
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
            throw new InvalidOperationException($"Invalid RUNS entry '{text}'. Expected '[ ... ] = value' or '[ ... ] = [ ... ]'.");

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

        if (parameters.Count > HardwareVectorWidth)
            throw new InvalidOperationException($"Invalid RUNS entry '{text}'. Hardware fixtures support at most 8 parameters.");

        string expectedLiteral = match.Groups["expected"].Value.Trim();
        IReadOnlyList<uint> expectedOutputs = ParseExpectedHardwareOutputs(expectedLiteral, text);
        return new HardwareRunExpectation(ZeroFillHardwareParameters(parameters), parameterLiterals, expectedOutputs);
    }

    private static IReadOnlyList<uint> ParseExpectedHardwareOutputs(string expectedLiteral, string runText)
    {
        if (expectedLiteral.StartsWith('['))
        {
            if (!expectedLiteral.EndsWith(']'))
                throw new InvalidOperationException($"Invalid hardware literal '{expectedLiteral}'.");

            string contents = expectedLiteral[1..^1].Trim();
            if (contents.Length == 0)
                throw new InvalidOperationException($"Invalid RUNS entry '{runText}'. Expected output arrays must contain between 1 and 8 values.");

            string[] parts = contents.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Any(static part => part.Length == 0))
                throw new InvalidOperationException($"Invalid RUNS entry '{runText}'. Expected output arrays must be comma-separated values.");

            if (parts.Length > HardwareVectorWidth)
                throw new InvalidOperationException($"Invalid RUNS entry '{runText}'. Hardware fixtures support at most 8 expected outputs.");

            List<uint> values = [];
            foreach (string part in parts)
                values.Add(ParseHardwareLiteral(part));

            return ZeroFillHardwareValues(values);
        }

        return ZeroFillHardwareValues([ParseHardwareLiteral(expectedLiteral)]);
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

    /// <summary>Captures the parsed header region and remaining body text of an encoded fixture.</summary>
    private sealed class HeaderScanResult
    {
        private HeaderScanResult(IReadOnlyList<HeaderLine> headerLines, string bodyText, bool hasDirectiveHeader)
        {
            HeaderLines = headerLines;
            BodyText = bodyText;
            HasDirectiveHeader = hasDirectiveHeader;
        }

        /// <summary>Gets the scanned header lines in source order.</summary>
        public IReadOnlyList<HeaderLine> HeaderLines { get; }

        /// <summary>Gets the remaining fixture body text after the header.</summary>
        public string BodyText { get; }

        /// <summary>Gets whether the fixture began with a directive-style header.</summary>
        public bool HasDirectiveHeader { get; }

        /// <summary>Scans the fixture text into header lines and executable body text.</summary>
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
