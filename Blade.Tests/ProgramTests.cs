using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Blade.Reports;
using Blade.Source;

namespace Blade.Tests;

[TestFixture]
public class ProgramTests
{
    private static readonly object ConsoleLock = new();

    private static Type CommandLineOptionsType => typeof(SourceText).Assembly.GetType("Blade.CommandLineOptions", throwOnError: true)!;
    private static Type CommandLineParserType => typeof(SourceText).Assembly.GetType("Blade.CommandLineParser", throwOnError: true)!;

    private static (T Result, string StdOut, string StdErr) CaptureConsole<T>(Func<T> action)
    {
        lock (ConsoleLock)
        {
            TextWriter originalOut = Console.Out;
            TextWriter originalErr = Console.Error;
            StringWriter stdout = new();
            StringWriter stderr = new();

            Console.SetOut(stdout);
            Console.SetError(stderr);
            try
            {
                T result = action();
                return (result, stdout.ToString(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }
    }

    private static object? ParseOptions(params string[] args)
    {
        MethodInfo parse = CommandLineParserType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)!;
        return parse.Invoke(null, [args]);
    }

    private static T GetProperty<T>(object instance, string name)
    {
        return (T)CommandLineOptionsType.GetProperty(name)!.GetValue(instance)!;
    }

    private static int InvokeEntryPoint(string[] args)
    {
        MethodInfo entryPoint = typeof(SourceText).Assembly.EntryPoint!;
        object? result = entryPoint.GetParameters().Length == 0
            ? entryPoint.Invoke(null, null)
            : entryPoint.Invoke(null, [args]);

        return result switch
        {
            null => 0,
            int exitCode => exitCode,
            Task<int> intTask => intTask.GetAwaiter().GetResult(),
            Task task => AwaitTask(task),
            _ => throw new InvalidOperationException($"Unexpected entry point return type: {result.GetType()}"),
        };
    }

    private static int AwaitTask(Task task)
    {
        task.GetAwaiter().GetResult();
        return 0;
    }

    private static string MatchGroup(string text, string pattern)
    {
        Match match = Regex.Match(text, pattern);
        Assert.That(match.Success, Is.True, $"Pattern did not match: {pattern}");
        return match.Groups[1].Value;
    }

    [Test]
    public void CommandLineOptions_Parse_RecognizesHtmlThemeProperty()
    {
        object? options = ParseOptions("input.blade", "--report", "html,report.html,theme=vscode");

        Assert.That(options, Is.Not.Null);
        IReadOnlyList<ReportTarget> reportTargets = GetProperty<IReadOnlyList<ReportTarget>>(options!, "ReportTargets");
        Assert.That(reportTargets, Has.Count.EqualTo(1));
        Assert.That(reportTargets[0].Writer, Is.TypeOf<HtmlReportWriter>());
        Assert.That(reportTargets[0].Properties["theme"], Is.EqualTo("vscode"));
    }

    [Test]
    public void CommandLineOptions_Parse_RejectsRemovedFlagsAndBadReports()
    {
        (object? removedOption, _, string removedErr) = CaptureConsole(() => ParseOptions("input.blade", "--json"));
        Assert.That(removedOption, Is.Null);
        Assert.That(removedErr, Does.Contain("has been removed"));

        (object? missingReport, _, string missingReportErr) = CaptureConsole(() => ParseOptions("input.blade", "--report"));
        Assert.That(missingReport, Is.Null);
        Assert.That(missingReportErr, Does.Contain("missing value for --report"));

        (object? badFormat, _, string badFormatErr) = CaptureConsole(() => ParseOptions("input.blade", "--report", "yaml,out.yaml"));
        Assert.That(badFormat, Is.Null);
        Assert.That(badFormatErr, Does.Contain("unknown report format"));

        (object? malformedProperty, _, string malformedPropertyErr) = CaptureConsole(() => ParseOptions("input.blade", "--report", "html,out.html,theme"));
        Assert.That(malformedProperty, Is.Null);
        Assert.That(malformedPropertyErr, Does.Contain("expected <key>=<value>"));

        (object? emptyPropertyKey, _, string emptyPropertyKeyErr) = CaptureConsole(() => ParseOptions("input.blade", "--report", "html,out.html,=dark"));
        Assert.That(emptyPropertyKey, Is.Null);
        Assert.That(emptyPropertyKeyErr, Does.Contain("must not be empty"));

        (object? duplicatePropertyKey, _, string duplicatePropertyKeyErr) = CaptureConsole(() => ParseOptions("input.blade", "--report", "html,out.html,theme=dark,theme=light"));
        Assert.That(duplicatePropertyKey, Is.Null);
        Assert.That(duplicatePropertyKeyErr, Does.Contain("duplicate report property 'theme'"));

        (object? unknownPropertyKey, _, string unknownPropertyKeyErr) = CaptureConsole(() => ParseOptions("input.blade", "--report", "html,out.html,palette=dark"));
        Assert.That(unknownPropertyKey, Is.Null);
        Assert.That(unknownPropertyKeyErr, Does.Contain("unknown report property 'palette'"));

        (object? duplicateStdout, _, string duplicateStdoutErr) = CaptureConsole(() => ParseOptions("input.blade", "--report", "text,-", "--report", "json,-"));
        Assert.That(duplicateStdout, Is.Null);
        Assert.That(duplicateStdoutErr, Does.Contain("stdout"));
    }

    [Test]
    public void EntryPoint_ReturnsErrorForMissingFile()
    {
        (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint(["/no/such/file.blade"]));

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(stdout, Is.Empty);
        Assert.That(stderr, Does.Contain("file not found"));
    }

    [Test]
    public void EntryPoint_PrintsDiagnosticsForInvalidSource()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-invalid-{Guid.NewGuid():N}.blade");
        File.WriteAllText(filePath, "cog task main { x = 1; }");

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath, "--report", "text,-"]));

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("status: failed"));
            Assert.That(stdout, Does.Contain("E0202"));
            Assert.That(stdout, Does.Contain(filePath));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Test]
    public void EntryPoint_DefaultsToBareFinalAssembly()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-stdout-{Guid.NewGuid():N}.blade");
        File.WriteAllText(filePath, "cog task main { }");

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath]));

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("DAT"));
            Assert.That(stdout, Does.Not.Contain("status:"));
            Assert.That(stdout, Does.Not.Contain("metrics:"));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Test]
    public void EntryPoint_WritesTextReportToStdout()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-report-{Guid.NewGuid():N}.blade");
        File.WriteAllText(filePath, "cog task main { }");

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath, "--report", "text,-"]));

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("status: succeeded"));
            Assert.That(stdout, Does.Contain("' 00_bound.ir"));
            Assert.That(stdout, Does.Contain("' 40_final.spin2"));
            Assert.That(stdout, Does.Contain("metrics:"));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Test]
    public void EntryPoint_CanWriteHtmlAndJsonReportsToFiles()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-report-file-{Guid.NewGuid():N}.blade");
        string htmlPath = Path.Combine(Path.GetTempPath(), $"blade-report-{Guid.NewGuid():N}.html");
        string jsonPath = Path.Combine(Path.GetTempPath(), $"blade-report-{Guid.NewGuid():N}.json");
        File.WriteAllText(filePath, "cog task main { }");

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath, "--report", $"html,{htmlPath}", "--report", $"json,{jsonPath}"]));

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stdout, Is.Empty);
            Assert.That(stderr, Is.Empty);
            string html = File.ReadAllText(htmlPath);
            Assert.That(html, Does.Contain("<!DOCTYPE html>"));
            Assert.That(html, Does.Contain("<table class=\"metrics-table\">"));
            Assert.That(html, Does.Not.Contain(">Status<"));
            Assert.That(html, Does.Not.Contain("<section class=\"diagnostics\">"));
            Assert.That(html, Does.Contain("<label><input type=\"checkbox\" checked>Final Assembly</label>"));

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(jsonPath));
            JsonElement root = document.RootElement;
            Assert.That(root.GetProperty("status").GetString(), Is.EqualTo("succeeded"));
            Assert.That(root.GetProperty("sections").EnumerateArray().Any(section => section.GetProperty("id").GetString() == "final-asm"), Is.True);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
            if (File.Exists(htmlPath))
                File.Delete(htmlPath);
            if (File.Exists(jsonPath))
                File.Delete(jsonPath);
        }
    }

    [Test]
    public void EntryPoint_CanWriteHtmlReportWithVsCodeTheme()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-report-theme-{Guid.NewGuid():N}.blade");
        string htmlPath = Path.Combine(Path.GetTempPath(), $"blade-report-theme-{Guid.NewGuid():N}.html");
        File.WriteAllText(filePath, "cog task main { }");

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath, "--report", $"html,{htmlPath},theme=vscode"]));

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stdout, Is.Empty);
            Assert.That(stderr, Is.Empty);

            string html = File.ReadAllText(htmlPath);
            Assert.That(html, Does.Contain("<body class=\"theme-vscode\" style=\""));
            Assert.That(html, Does.Contain("--vscode-editor-background: #000000;"));
            Assert.That(html, Does.Contain("--report-background: var(--vscode-editor-background);"));
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
            if (File.Exists(htmlPath))
                File.Delete(htmlPath);
        }
    }

    [Test]
    public void EntryPoint_RejectsUnknownHtmlTheme()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-report-theme-invalid-{Guid.NewGuid():N}.blade");
        File.WriteAllText(filePath, "cog task main { }");

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath, "--report", "html,-,theme=midnight"]));

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stdout, Is.Empty);
            Assert.That(stderr, Does.Contain("unknown html report theme 'midnight'"));
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [Test]
    public void EntryPoint_WritesFailureAsJsonReport()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-json-fail-{Guid.NewGuid():N}.blade");
        File.WriteAllText(filePath, "cog task main { x = 1; }");

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath, "--report", "json,-"]));

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stderr, Is.Empty);

            using JsonDocument document = JsonDocument.Parse(stdout);
            JsonElement root = document.RootElement;
            Assert.That(root.GetProperty("status").GetString(), Is.EqualTo("failed"));
            Assert.That(root.GetProperty("diagnostics").GetArrayLength(), Is.EqualTo(1));
            Assert.That(root.GetProperty("sections").GetArrayLength(), Is.GreaterThanOrEqualTo(1));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Test]
    public void EntryPoint_WritesHtmlDiagnosticsAboveTabView()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-html-fail-{Guid.NewGuid():N}.blade");
        File.WriteAllText(filePath, "cog task main { x = 1; }");

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath, "--report", "html,-"]));

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("<section class=\"diagnostics\">"));
            Assert.That(stdout, Does.Contain("href=\"file://"));
            Assert.That(stdout.IndexOf("<section class=\"diagnostics\">", StringComparison.Ordinal), Is.LessThan(stdout.IndexOf("<main>", StringComparison.Ordinal)));
            Assert.That(stdout, Does.Contain("<table class=\"metrics-table\">"));
            Assert.That(stdout, Does.Not.Contain(">Status<"));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Test]
    public void EntryPoint_CanWriteAllReportFormatsForConditionalBranchProgram()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-conditional-{Guid.NewGuid():N}.blade");
        string textPath = Path.Combine(Path.GetTempPath(), $"blade-conditional-{Guid.NewGuid():N}.txt");
        string htmlPath = Path.Combine(Path.GetTempPath(), $"blade-conditional-{Guid.NewGuid():N}.html");
        string jsonPath = Path.Combine(Path.GetTempPath(), $"blade-conditional-{Guid.NewGuid():N}.json");
        File.WriteAllText(filePath, """
            cog task main {
                cog var a: u32 = 0;
                cog var b: u32 = 0;

                if (a == b) {
                    asm volatile {
                        COGATN #1
                    };
                }
                else {
                    asm volatile {
                        COGATN #2
                    };
                }
            }
            """);

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([
                filePath,
                "--report", $"text,{textPath}",
                "--report", $"html,{htmlPath}",
                "--report", $"json,{jsonPath}",
            ]));

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stdout, Is.Empty);
            Assert.That(stderr, Is.Empty);

            string textReport = File.ReadAllText(textPath);
            string htmlReport = File.ReadAllText(htmlPath);
            using JsonDocument jsonReport = JsonDocument.Parse(File.ReadAllText(jsonPath));

            Assert.That(textReport, Does.Contain("' 35_image_memory_maps.ir"));
            Assert.That(textReport, Does.Contain("image main entry mode=Cog"));
            Assert.That(htmlReport, Does.Contain("<!DOCTYPE html>"));
            Assert.That(htmlReport, Does.Contain("35_image_memory_maps.ir"));
            Assert.That(jsonReport.RootElement.GetProperty("sections").EnumerateArray().Any(section => section.GetProperty("id").GetString() == "image-memory-maps"), Is.True);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
            if (File.Exists(textPath))
                File.Delete(textPath);
            if (File.Exists(htmlPath))
                File.Delete(htmlPath);
            if (File.Exists(jsonPath))
                File.Delete(jsonPath);
        }
    }

    [Test]
    public void EntryPoint_HtmlReport_UsesDistinctSemanticIdsAcrossMirLirAndAsmir()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-conditional-html-{Guid.NewGuid():N}.blade");
        string htmlPath = Path.Combine(Path.GetTempPath(), $"blade-conditional-html-{Guid.NewGuid():N}.html");
        File.WriteAllText(filePath, """
            cog task main {
                cog var a: u32 = 0;
                cog var b: u32 = 0;

                if (a == b) {
                    asm volatile {
                        COGATN #1
                    };
                }
                else {
                    asm volatile {
                        COGATN #2
                    };
                }
            }
            """);

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([
                filePath,
                "--report", $"html,{htmlPath}",
            ]));

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stdout, Is.Empty);
            Assert.That(stderr, Is.Empty);

            string html = File.ReadAllText(htmlPath);
            string mirId = MatchGroup(html, "<span class=\"var\" data-symbol=\"([^\"]+)\">%v0</span>");
            string lirId = MatchGroup(html, "<span class=\"var\" data-symbol=\"([^\"]+)\">%r0</span>");
            string asmirId = MatchGroup(html, "<span class=\"var\" data-symbol=\"([^\"]+)\">main_r\\d+</span>");

            Assert.That(mirId, Is.Not.EqualTo(lirId));
            Assert.That(mirId, Is.Not.EqualTo(asmirId));
            Assert.That(lirId, Is.Not.EqualTo(asmirId));
            Assert.That(html, Does.Match("binary\\.Equals</span> <span class=\"var\" data-symbol=\"[^\"]+\">%r0</span><span class=\"punct\">,</span> <span class=\"var\" data-symbol=\"[^\"]+\">%r1</span>"));
            Assert.That(html, Does.Match("CMP</span> <span class=\"var\" data-symbol=\"[^\"]+\">main_r\\d+</span><span class=\"punct\">,</span> <span class=\"var\" data-symbol=\"[^\"]+\">main_r\\d+</span> <span class=\"kw\">WZ</span><span class=\"punct\">=</span><span class=\"var\" data-symbol=\"[^\"]+\">%f0</span>"));
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
            if (File.Exists(htmlPath))
                File.Delete(htmlPath);
        }
    }

    [Test]
    public void EntryPoint_ReportsOutputWriteFailure()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-output-fail-{Guid.NewGuid():N}.blade");
        string outputDirectory = Path.Combine(Path.GetTempPath(), $"blade-output-dir-{Guid.NewGuid():N}");
        File.WriteAllText(filePath, "cog task main { }");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath, "--report", $"json,{outputDirectory}"]));

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stdout, Is.Empty);
            Assert.That(stderr, Does.Contain("failed to write report output"));
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private sealed class SpyReportWriter(IReadOnlySet<string> propertyKeys) : IReportWriter
    {
        public IReadOnlyDictionary<string, string>? ReceivedProperties { get; private set; }

        public IReadOnlySet<string> PropertyKeys { get; } = propertyKeys;

        public void Write(TextWriter writer, CompilationOutput report, IReadOnlyDictionary<string, string> properties)
        {
            Requires.NotNull(writer);
            Requires.NotNull(properties);
            this.ReceivedProperties = new Dictionary<string, string>(properties, StringComparer.Ordinal);
        }
    }
}
