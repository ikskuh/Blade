using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
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

        if (result is null)
            return 0;
        if (result is int exitCode)
            return exitCode;
        if (result is Task<int> intTask)
            return intTask.GetAwaiter().GetResult();
        if (result is Task task)
        {
            task.GetAwaiter().GetResult();
            return 0;
        }

        throw new InvalidOperationException($"Unexpected entry point return type: {result.GetType()}");
    }

    [Test]
    public void CommandLineOptions_Parse_RecognizesAllSupportedFlags()
    {
        object? options = ParseOptions(
            "input.blade",
            "--dump-bound",
            "--dump-mir-preopt",
            "--dump-mir",
            "--dump-lir-preopt",
            "--dump-lir",
            "--dump-asmir-preopt",
            "--dump-asmir",
            "--dump-final-asm",
            "--json",
            "--output",
            "report.json");

        Assert.That(options, Is.Not.Null);
        Assert.That(GetProperty<string>(options!, "FilePath"), Is.EqualTo("input.blade"));
        Assert.That(GetProperty<bool>(options!, "DumpBound"), Is.True);
        Assert.That(GetProperty<bool>(options!, "DumpMirPreOptimization"), Is.True);
        Assert.That(GetProperty<bool>(options!, "DumpMir"), Is.True);
        Assert.That(GetProperty<bool>(options!, "DumpLirPreOptimization"), Is.True);
        Assert.That(GetProperty<bool>(options!, "DumpLir"), Is.True);
        Assert.That(GetProperty<bool>(options!, "DumpAsmirPreOptimization"), Is.True);
        Assert.That(GetProperty<bool>(options!, "DumpAsmir"), Is.True);
        Assert.That(GetProperty<bool>(options!, "DumpFinalAsm"), Is.True);
        Assert.That(GetProperty<bool>(options!, "Json"), Is.True);
        Assert.That(GetProperty<string?>(options!, "OutputPath"), Is.EqualTo("report.json"));
        Assert.That(GetProperty<string?>(options!, "DumpDirectory"), Is.Null);
    }

    [Test]
    public void CommandLineOptions_Parse_ExpandsDumpAll()
    {
        object? options = ParseOptions("input.blade", "--dump-all");

        Assert.That(options, Is.Not.Null);
        Assert.That(GetProperty<bool>(options!, "DumpBound"), Is.True);
        Assert.That(GetProperty<bool>(options!, "DumpMirPreOptimization"), Is.True);
        Assert.That(GetProperty<bool>(options!, "DumpMir"), Is.True);
        Assert.That(GetProperty<bool>(options!, "DumpLirPreOptimization"), Is.True);
        Assert.That(GetProperty<bool>(options!, "DumpLir"), Is.True);
        Assert.That(GetProperty<bool>(options!, "DumpAsmirPreOptimization"), Is.True);
        Assert.That(GetProperty<bool>(options!, "DumpAsmir"), Is.True);
        Assert.That(GetProperty<bool>(options!, "DumpFinalAsm"), Is.True);
    }


    [Test]
    public void CommandLineOptions_Parse_ReportsUsageAndErrors()
    {
        (object? noArgs, _, string noArgsErr) = CaptureConsole(() => ParseOptions());
        Assert.That(noArgs, Is.Null);
        Assert.That(noArgsErr, Does.Contain("Usage: blade"));
        Assert.That(noArgsErr, Does.Contain("--comptime-fuel=<positive-integer>"));

        (object? missingDumpDir, _, string missingDumpDirErr) = CaptureConsole(() => ParseOptions("input.blade", "--dump-dir"));
        Assert.That(missingDumpDir, Is.Null);
        Assert.That(missingDumpDirErr, Does.Contain("missing value for --dump-dir"));

        (object? missingOutput, _, string missingOutputErr) = CaptureConsole(() => ParseOptions("input.blade", "--output"));
        Assert.That(missingOutput, Is.Null);
        Assert.That(missingOutputErr, Does.Contain("missing value for --output"));

        (object? unknownOption, _, string unknownOptionErr) = CaptureConsole(() => ParseOptions("input.blade", "--bogus"));
        Assert.That(unknownOption, Is.Null);
        Assert.That(unknownOptionErr, Does.Contain("unknown option '--bogus'"));

        (object? unknownMirOpt, _, string unknownMirOptErr) = CaptureConsole(() => ParseOptions("input.blade", "-fmir-opt=not-real"));
        Assert.That(unknownMirOpt, Is.Null);
        Assert.That(unknownMirOptErr, Does.Contain("unknown mir optimization"));

        (object? invalidComptimeFuel, _, string invalidComptimeFuelErr) = CaptureConsole(() => ParseOptions("input.blade", "--comptime-fuel=0"));
        Assert.That(invalidComptimeFuel, Is.Null);
        Assert.That(invalidComptimeFuelErr, Does.Contain("invalid comptime fuel '0'"));

        (object? multipleFiles, _, string multipleFilesErr) = CaptureConsole(() => ParseOptions("a.blade", "b.blade"));
        Assert.That(multipleFiles, Is.Null);
        Assert.That(multipleFilesErr, Does.Contain("multiple input files"));

        (object? jsonWithDumpDir, _, string jsonWithDumpDirErr) = CaptureConsole(() => ParseOptions("input.blade", "--json", "--dump-dir", "out"));
        Assert.That(jsonWithDumpDir, Is.Null);
        Assert.That(jsonWithDumpDirErr, Does.Contain("--json cannot be combined with --dump-dir"));

        (object? outputWithDumpDir, _, string outputWithDumpDirErr) = CaptureConsole(() => ParseOptions("input.blade", "--output", "report.txt", "--dump-dir", "out"));
        Assert.That(outputWithDumpDir, Is.Null);
        Assert.That(outputWithDumpDirErr, Does.Contain("--output cannot be combined with --dump-dir"));

        (object? missingInput, _, string missingInputErr) = CaptureConsole(() => ParseOptions("--dump-bound"));
        Assert.That(missingInput, Is.Null);
        Assert.That(missingInputErr, Does.Contain("missing input file"));
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
    public void EntryPoint_ReturnsUsageErrorWhenNoArgumentsAreProvided()
    {
        (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([]));

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(stdout, Is.Empty);
        Assert.That(stderr, Does.Contain("Usage: blade"));
    }

    [Test]
    public void EntryPoint_PrintsDiagnosticsForInvalidSource()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-invalid-{Guid.NewGuid():N}.blade");
        File.WriteAllText(filePath, "x = 1;");

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath]));

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stdout, Does.Contain(filePath));
            Assert.That(stdout, Does.Contain("E0202"));
            Assert.That(stdout, Does.Contain(":1:1"));
            Assert.That(stderr, Is.Empty);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Test]
    public void EntryPoint_PrintsDiagnosticForUnknownBuiltin()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-unknown-builtin-{Guid.NewGuid():N}.blade");
        File.WriteAllText(filePath, "@askdjsad(1);");

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath]));

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stdout, Does.Contain("E0256"));
            Assert.That(stdout, Does.Contain("Unknown builtin '@askdjsad'."));
            Assert.That(stderr, Is.Empty);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Test]
    public void EntryPoint_PrintsDiagnosticForInvalidUtf8()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-invalid-utf8-{Guid.NewGuid():N}.blade");
        File.WriteAllBytes(filePath, [0x80, 0x61]);

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath]));

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stdout, Does.Contain(filePath));
            Assert.That(stdout, Does.Contain("E0007"));
            Assert.That(stdout, Does.Contain("Source file is not valid UTF-8."));
            Assert.That(stderr, Is.Empty);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Test]
    public void EntryPoint_PrintsDiagnosticForForbiddenControlCharacter()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-invalid-control-{Guid.NewGuid():N}.blade");
        File.WriteAllText(filePath, "reg var x:\u0001 u32 = 1;");

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath]));

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stdout, Does.Contain(filePath));
            Assert.That(stdout, Does.Contain("E0008"));
            Assert.That(stdout, Does.Contain("Control character U+0001 is not allowed in Blade source files."));
            Assert.That(stderr, Is.Empty);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Test]
    public void EntryPoint_CanWriteDumpsToDirectory()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-valid-{Guid.NewGuid():N}.blade");
        string dumpDir = Path.Combine(Path.GetTempPath(), $"blade-dumps-{Guid.NewGuid():N}");
        File.WriteAllText(filePath, "reg var x: u32 = 1;");

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath, "--dump-bound", "--dump-final-asm", "--dump-dir", dumpDir]));

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stderr, Is.Empty);
            Assert.That(File.Exists(Path.Combine(dumpDir, "00_bound.ir")), Is.True);
            Assert.That(File.Exists(Path.Combine(dumpDir, "40_final.spin2")), Is.True);
            Assert.That(stdout, Does.Contain("tokens :"));
            Assert.That(stdout, Does.Contain("errors : 0"));
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
            if (Directory.Exists(dumpDir))
                Directory.Delete(dumpDir, recursive: true);
        }
    }

    [Test]
    public void EntryPoint_WritesRequestedDumpToStdoutWithoutDumpDirectory()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-stdout-{Guid.NewGuid():N}.blade");
        File.WriteAllText(filePath, "reg var x: u32 = 1;");

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath, "--dump-bound"]));

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("' 00_bound.ir"));
            Assert.That(stdout, Does.Contain("Program"));
            Assert.That(stdout, Does.Contain("errors : 0"));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Test]
    public void EntryPoint_WritesMultipleDumpsToStdoutWithSeparators()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-stdout-multi-{Guid.NewGuid():N}.blade");
        File.WriteAllText(filePath, "reg var x: u32 = 1;");

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath, "--dump-bound", "--dump-final-asm"]));

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("' 00_bound.ir"));
            Assert.That(stdout, Does.Contain("' 40_final.spin2"));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Test]
    public void EntryPoint_WritesRequestedDumpAsJsonToStdout()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-json-stdout-{Guid.NewGuid():N}.blade");
        File.WriteAllText(filePath, "reg var x: u32 = 1;");

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath, "--dump-bound", "--json"]));

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stderr, Is.Empty);

            using JsonDocument document = JsonDocument.Parse(stdout);
            JsonElement root = document.RootElement;
            Assert.That(root.GetProperty("success").GetBoolean(), Is.True);
            Assert.That(root.GetProperty("diagnostics").GetArrayLength(), Is.EqualTo(0));
            Assert.That(root.GetProperty("dumps").GetProperty("bound").GetString(), Does.Contain("Program"));
            Assert.That(root.GetProperty("dumps").GetProperty("asmir").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(root.GetProperty("result").GetString(), Does.Contain("org 0"));
            Assert.That(root.GetProperty("metrics").GetProperty("token_count").GetInt32(), Is.GreaterThan(0));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Test]
    public void EntryPoint_CanWriteTextReportToFile()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-output-file-{Guid.NewGuid():N}.blade");
        string outputPath = Path.Combine(Path.GetTempPath(), $"blade-output-file-{Guid.NewGuid():N}.txt");
        File.WriteAllText(filePath, "reg var x: u32 = 1;");

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath, "--dump-bound", "--output", outputPath]));

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stdout, Is.Empty);
            Assert.That(stderr, Is.Empty);

            string report = File.ReadAllText(outputPath);
            Assert.That(report, Does.Contain("' 00_bound.ir"));
            Assert.That(report, Does.Contain("errors : 0"));
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Test]
    public void EntryPoint_ReportsTextOutputWriteFailure()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-output-fail-{Guid.NewGuid():N}.blade");
        string outputDirectory = Path.Combine(Path.GetTempPath(), $"blade-output-dir-{Guid.NewGuid():N}");
        File.WriteAllText(filePath, "reg var x: u32 = 1;");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath, "--dump-bound", "--output", outputDirectory]));

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stdout, Is.Empty);
            Assert.That(stderr, Does.Contain("failed to write output"));
            Assert.That(stderr, Does.Contain(outputDirectory));
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Test]
    public void EntryPoint_CanWriteJsonReportToFile()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-json-file-{Guid.NewGuid():N}.blade");
        string outputPath = Path.Combine(Path.GetTempPath(), $"blade-json-file-{Guid.NewGuid():N}.json");
        File.WriteAllText(filePath, "reg var x: u32 = 1;");

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath, "--dump-bound", "--json", "--output", outputPath]));

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stdout, Is.Empty);
            Assert.That(stderr, Is.Empty);

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(outputPath));
            JsonElement root = document.RootElement;
            Assert.That(root.GetProperty("success").GetBoolean(), Is.True);
            Assert.That(root.GetProperty("dumps").GetProperty("bound").GetString(), Does.Contain("Program"));
            Assert.That(root.GetProperty("result").GetString(), Does.Contain("org 0"));
            Assert.That(root.GetProperty("metrics").GetProperty("token_count").GetInt32(), Is.GreaterThan(0));
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Test]
    public void EntryPoint_ReportsJsonOutputWriteFailure()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-json-output-fail-{Guid.NewGuid():N}.blade");
        string outputDirectory = Path.Combine(Path.GetTempPath(), $"blade-json-output-dir-{Guid.NewGuid():N}");
        File.WriteAllText(filePath, "reg var x: u32 = 1;");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath, "--dump-bound", "--json", "--output", outputDirectory]));

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stdout, Is.Empty);
            Assert.That(stderr, Does.Contain("failed to write output"));
            Assert.That(stderr, Does.Contain(outputDirectory));
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Test]
    public void EntryPoint_TreatsDashOutputPathAsStdout()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-json-dash-{Guid.NewGuid():N}.blade");
        File.WriteAllText(filePath, "reg var x: u32 = 1;");

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath, "--dump-bound", "--json", "--output", "-"]));

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stderr, Is.Empty);

            using JsonDocument document = JsonDocument.Parse(stdout);
            JsonElement root = document.RootElement;
            Assert.That(root.GetProperty("success").GetBoolean(), Is.True);
            Assert.That(root.GetProperty("dumps").GetProperty("bound").GetString(), Does.Contain("Program"));
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [Test]
    public void EntryPoint_WritesFailureAsJsonEnvelope()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"blade-json-fail-{Guid.NewGuid():N}.blade");
        File.WriteAllText(filePath, "x = 1;");

        try
        {
            (int exitCode, string stdout, string stderr) = CaptureConsole(() => InvokeEntryPoint([filePath, "--json"]));

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stderr, Is.Empty);

            using JsonDocument document = JsonDocument.Parse(stdout);
            JsonElement root = document.RootElement;
            Assert.That(root.GetProperty("success").GetBoolean(), Is.False);
            Assert.That(root.GetProperty("diagnostics").GetArrayLength(), Is.EqualTo(1));
            JsonElement diagnostic = root.GetProperty("diagnostics")[0];
            Assert.That(diagnostic.GetProperty("file").GetString(), Is.EqualTo(filePath));
            Assert.That(diagnostic.GetProperty("line").GetInt32(), Is.EqualTo(1));
            Assert.That(diagnostic.GetProperty("code").GetString(), Is.EqualTo("E0202"));
            Assert.That(root.GetProperty("result").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(root.GetProperty("dumps").GetProperty("bound").ValueKind, Is.EqualTo(JsonValueKind.Null));
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [Test]
    public void CommandLineOptions_Parse_RejectsMalformedOptimizationLists()
    {
        (object? missingCsv, _, string missingCsvErr) = CaptureConsole(() => ParseOptions("input.blade", "-fmir-opt="));
        Assert.That(missingCsv, Is.Null);
        Assert.That(missingCsvErr, Does.Contain("missing optimization list"));

        (object? emptyCsv, _, string emptyCsvErr) = CaptureConsole(() => ParseOptions("input.blade", "-fmir-opt=, ,"));
        Assert.That(emptyCsv, Is.Null);
        Assert.That(emptyCsvErr, Does.Contain("missing optimization list"));
    }

    [Test]
    public void CommandLineOptions_Parse_RejectsInvalidModuleMappings()
    {
        (object? missingEquals, _, string missingEqualsErr) = CaptureConsole(() => ParseOptions("input.blade", "--module=extmod"));
        Assert.That(missingEquals, Is.Null);
        Assert.That(missingEqualsErr, Does.Contain("invalid module specification"));

        (object? emptyName, _, string emptyNameErr) = CaptureConsole(() => ParseOptions("input.blade", "--module==path.blade"));
        Assert.That(emptyName, Is.Null);
        Assert.That(emptyNameErr, Does.Contain("invalid module specification"));

        (object? emptyPath, _, string emptyPathErr) = CaptureConsole(() => ParseOptions("input.blade", "--module=extmod="));
        Assert.That(emptyPath, Is.Null);
        Assert.That(emptyPathErr, Does.Contain("invalid module specification"));

        (object? blankPathAfterTrim, _, string blankPathAfterTrimErr) = CaptureConsole(() => ParseOptions("input.blade", "--module=extmod=   "));
        Assert.That(blankPathAfterTrim, Is.Null);
        Assert.That(blankPathAfterTrimErr, Does.Contain("invalid module specification"));

        (object? blankNameAfterTrim, _, string blankNameAfterTrimErr) = CaptureConsole(() => ParseOptions("input.blade", "--module=   =path.blade"));
        Assert.That(blankNameAfterTrim, Is.Null);
        Assert.That(blankNameAfterTrimErr, Does.Contain("invalid module specification"));
    }

    [Test]
    public void CommandLineOptions_Parse_ReportsUnknownOptimizationPerStage()
    {
        (object? badNoMir, _, string badNoMirErr) = CaptureConsole(() => ParseOptions("input.blade", "-fno-mir-opt=bad"));
        Assert.That(badNoMir, Is.Null);
        Assert.That(badNoMirErr, Does.Contain("unknown mir optimization"));

        (object? badLir, _, string badLirErr) = CaptureConsole(() => ParseOptions("input.blade", "-flir-opt=bad"));
        Assert.That(badLir, Is.Null);
        Assert.That(badLirErr, Does.Contain("unknown lir optimization"));

        (object? badNoLir, _, string badNoLirErr) = CaptureConsole(() => ParseOptions("input.blade", "-fno-lir-opt=bad"));
        Assert.That(badNoLir, Is.Null);
        Assert.That(badNoLirErr, Does.Contain("unknown lir optimization"));

        (object? badAsmir, _, string badAsmirErr) = CaptureConsole(() => ParseOptions("input.blade", "-fasmir-opt=bad"));
        Assert.That(badAsmir, Is.Null);
        Assert.That(badAsmirErr, Does.Contain("unknown asmir optimization"));

        (object? badNoAsmir, _, string badNoAsmirErr) = CaptureConsole(() => ParseOptions("input.blade", "-fno-asmir-opt=bad"));
        Assert.That(badNoAsmir, Is.Null);
        Assert.That(badNoAsmirErr, Does.Contain("unknown asmir optimization"));
    }

}
