using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Blade.Diagnostics;
using Blade.Source;
using Blade.Syntax;
using Blade.Syntax.Nodes;

namespace Blade.Tests;

[TestFixture]
public class AcceptRejectTests
{
    private static string TestDataPath => Path.Combine(TestContext.CurrentContext.TestDirectory);

    private static CompilationUnitSyntax ParseFile(string filePath, out DiagnosticBag diagnostics)
    {
        string text = File.ReadAllText(filePath);
        SourceText source = new(text, filePath);
        diagnostics = new DiagnosticBag();
        Parser parser = Parser.Create(source, diagnostics);
        return parser.ParseCompilationUnit();
    }

    private static HashSet<string> ExtractExpectedCodes(string filePath)
    {
        string firstLine = File.ReadLines(filePath).FirstOrDefault() ?? "";
        Match match = Regex.Match(firstLine, @"^//\s*(.+)$");
        if (!match.Success)
            return new HashSet<string>();

        return match.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet();
    }

    // ── Accept tests ──

    private static IEnumerable<string> AcceptFiles()
    {
        string dir = Path.Combine(TestDataPath, "Accept");
        if (!Directory.Exists(dir))
            yield break;
        foreach (string file in Directory.GetFiles(dir, "*.blade"))
            yield return Path.GetFileName(file);
    }

    [TestCaseSource(nameof(AcceptFiles))]
    public void AcceptFile_ParsesWithoutErrors(string fileName)
    {
        string filePath = Path.Combine(TestDataPath, "Accept", fileName);
        CompilationUnitSyntax unit = ParseFile(filePath, out DiagnosticBag diagnostics);

        Assert.That(unit, Is.Not.Null);
        if (diagnostics.Count > 0)
        {
            string errors = string.Join("\n", diagnostics.Select(d => d.ToString()));
            Assert.Fail($"Expected no diagnostics but got:\n{errors}");
        }
    }

    // ── Reject tests ──

    private static IEnumerable<string> RejectFiles()
    {
        string dir = Path.Combine(TestDataPath, "Reject");
        if (!Directory.Exists(dir))
            yield break;
        foreach (string file in Directory.GetFiles(dir, "*.blade"))
            yield return Path.GetFileName(file);
    }

    [TestCaseSource(nameof(RejectFiles))]
    public void RejectFile_EmitsExpectedDiagnostics(string fileName)
    {
        string filePath = Path.Combine(TestDataPath, "Reject", fileName);
        ParseFile(filePath, out DiagnosticBag diagnostics);

        HashSet<string> expectedCodes = ExtractExpectedCodes(filePath);
        Assert.That(expectedCodes, Is.Not.Empty, "Reject file must have expected diagnostic codes on the first line.");

        HashSet<string> actualCodes = diagnostics.Select(d => d.FormatCode()).ToHashSet();

        foreach (string expected in expectedCodes)
        {
            Assert.That(actualCodes, Does.Contain(expected),
                $"Expected diagnostic {expected} but got: [{string.Join(", ", actualCodes)}]");
        }
    }
}
