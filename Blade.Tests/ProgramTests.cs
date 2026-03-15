using System.IO;
using System.Linq;
using System.Reflection;
using Blade.Source;

namespace Blade.Tests;

[TestFixture]
public class ProgramTests
{
    private static readonly object ConsoleLock = new();

    private static Type CommandLineOptionsType => typeof(SourceText).Assembly.GetType("CommandLineOptions", throwOnError: true)!;

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
        MethodInfo parse = CommandLineOptionsType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)!;
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
            "--dump-dir",
            "out",
            "-fno-mir-opt=single-callsite-inline");

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
        Assert.That(GetProperty<string?>(options!, "DumpDirectory"), Is.EqualTo("out"));
        System.Collections.IEnumerable directives = GetProperty<System.Collections.IEnumerable>(options!, "OptimizationDirectives");
        Assert.That(directives.Cast<object>().Count(), Is.EqualTo(1));
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
    public void CommandLineOptions_Parse_RecognizesOptimizationToggleFlags()
    {
        object? options = ParseOptions(
            "input.blade",
            "-fno-asmir-opt=*",
            "-fasmir-opt=elide-nops",
            "-fno-mir-opt=const-prop",
            "-flir-opt=dce");

        Assert.That(options, Is.Not.Null);
        System.Collections.IEnumerable directives = GetProperty<System.Collections.IEnumerable>(options!, "OptimizationDirectives");
        Assert.That(directives.Cast<object>().Count(), Is.EqualTo(4));
    }

    [Test]
    public void CommandLineOptions_Parse_ReportsUsageAndErrors()
    {
        (object? noArgs, _, string noArgsErr) = CaptureConsole(() => ParseOptions());
        Assert.That(noArgs, Is.Null);
        Assert.That(noArgsErr, Does.Contain("Usage: blade"));

        (object? missingDumpDir, _, string missingDumpDirErr) = CaptureConsole(() => ParseOptions("input.blade", "--dump-dir"));
        Assert.That(missingDumpDir, Is.Null);
        Assert.That(missingDumpDirErr, Does.Contain("missing value for --dump-dir"));

        (object? unknownOption, _, string unknownOptionErr) = CaptureConsole(() => ParseOptions("input.blade", "--bogus"));
        Assert.That(unknownOption, Is.Null);
        Assert.That(unknownOptionErr, Does.Contain("unknown option '--bogus'"));

        (object? unknownMirOpt, _, string unknownMirOptErr) = CaptureConsole(() => ParseOptions("input.blade", "-fmir-opt=not-real"));
        Assert.That(unknownMirOpt, Is.Null);
        Assert.That(unknownMirOptErr, Does.Contain("unknown mir optimization"));

        (object? multipleFiles, _, string multipleFilesErr) = CaptureConsole(() => ParseOptions("a.blade", "b.blade"));
        Assert.That(multipleFiles, Is.Null);
        Assert.That(multipleFilesErr, Does.Contain("multiple input files"));

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
            Assert.That(stdout, Does.Contain(";; 00_bound.ir"));
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
            Assert.That(stdout, Does.Contain(";; 00_bound.ir"));
            Assert.That(stdout, Does.Contain(";; 40_final.spin2"));
        }
        finally
        {
            File.Delete(filePath);
        }
    }
}
