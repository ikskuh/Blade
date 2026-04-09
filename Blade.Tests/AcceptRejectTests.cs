using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Blade;
using Blade.Diagnostics;

namespace Blade.Tests;

[TestFixture]
public class AcceptRejectTests
{
    private static string TestDataPath => Path.Combine(TestContext.CurrentContext.TestDirectory);

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
        foreach (string file in Directory.GetFiles(dir, "*.blade", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(Path.Combine(TestDataPath, "Accept"), file);
            yield return relativePath;
        }
    }

    [TestCaseSource(nameof(AcceptFiles))]
    public void AcceptFile_ParsesWithoutErrors(string relativePath)
    {
        string filePath = Path.Combine(TestDataPath, "Accept", relativePath);
        CompilationResult result = CompilerDriver.CompileFile(filePath, new CompilationOptions
        {
            EmitIr = false,
        });

        Assert.That(result.Syntax, Is.Not.Null);
        if (result.Diagnostics.Count > 0)
        {
            string errors = string.Join("\n", result.Diagnostics.Select(d => d.ToString()));
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
        CompilationResult result = CompilerDriver.CompileFile(filePath, new CompilationOptions
        {
            EmitIr = false,
        });

        HashSet<string> expectedCodes = ExtractExpectedCodes(filePath);
        Assert.That(expectedCodes, Is.Not.Empty, "Reject file must have expected diagnostic codes on the first line.");

        HashSet<string> actualCodes = result.Diagnostics.Select(d => d.FormatCode()).ToHashSet();
        Assert.That(actualCodes, Is.EquivalentTo(expectedCodes),
            $"Expected diagnostics [{string.Join(", ", expectedCodes)}], but got [{string.Join(", ", actualCodes)}]");
    }
}
