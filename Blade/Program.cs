using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blade.Diagnostics;
using Blade.IR;
using Blade.Source;

namespace Blade;

internal static class Program
{
    public static int Main(string[] args)
    {
        CommandLineOptions? options = CommandLineOptions.Parse(args);
        if (options is null)
            return 1;

        if (!File.Exists(options.FilePath))
        {
            Console.Error.WriteLine($"error: file not found: {options.FilePath}");
            return 1;
        }

        Stopwatch sw = Stopwatch.StartNew();
        CompilationResult compilation = CompilerDriver.CompileFile(
            options.FilePath,
            new CompilationOptions
            {
                EnableSingleCallsiteInlining = options.EnableSingleCallsiteInlining,
                OptimizationDirectives = options.OptimizationDirectives,
                NamedModuleRoots = options.NamedModuleRoots,
                ComptimeFuel = options.ComptimeFuel,
            });
        sw.Stop();

        CompilationMetrics metrics = new()
        {
            TokenCount = compilation.TokenCount,
            MemberCount = compilation.Syntax.Members.Count,
            BoundFunctionCount = compilation.BoundProgram.Functions.Count,
            MirFunctionCount = compilation.IrBuildResult?.MirModule.Functions.Count ?? 0,
            TimeMs = sw.Elapsed.TotalMilliseconds,
        };

        if (options.Json)
        {
            JsonCompilationReport jsonReport = JsonReportBuilder.Build(compilation, options, metrics);
            if (!OutputWriter.TryWriteJson(options, jsonReport, out string? outputError))
            {
                Console.Error.WriteLine(outputError);
                return 1;
            }

            return jsonReport.Success ? 0 : 1;
        }

        foreach (Diagnostic diag in compilation.Diagnostics)
        {
            SourceLocation loc = compilation.Source.GetLocation(diag.Span.Start);
            Console.WriteLine($"{loc}: {diag}");
        }

        if (compilation.Diagnostics.Count > 0)
            return 1;

        if (compilation.IrBuildResult is null)
            return 1;

        DumpSelection dumpSelection = new()
        {
            DumpBound = options.DumpBound,
            DumpMirPreOptimization = options.DumpMirPreOptimization,
            DumpMir = options.DumpMir,
            DumpLirPreOptimization = options.DumpLirPreOptimization,
            DumpLir = options.DumpLir,
            DumpAsmirPreOptimization = options.DumpAsmirPreOptimization,
            DumpAsmir = options.DumpAsmir,
            DumpFinalAsm = options.DumpFinalAsm,
        };
        Dictionary<string, string> dumpContent = DumpContentBuilder.Build(dumpSelection, compilation.IrBuildResult);
        if (!OutputWriter.TryWriteText(options, dumpContent, metrics, errorCount: compilation.Diagnostics.Count, out string? textOutputError))
        {
            Console.Error.WriteLine(textOutputError);
            return 1;
        }

        return 0;
    }
}

internal sealed class CommandLineOptions
{
    private CommandLineOptions()
    {
    }

    public required string FilePath { get; init; }
    public bool DumpBound { get; init; }
    public bool DumpMirPreOptimization { get; init; }
    public bool DumpMir { get; init; }
    public bool DumpLirPreOptimization { get; init; }
    public bool DumpLir { get; init; }
    public bool DumpAsmirPreOptimization { get; init; }
    public bool DumpAsmir { get; init; }
    public bool DumpFinalAsm { get; init; }
    public string? DumpDirectory { get; init; }
    public bool Json { get; init; }
    public string? OutputPath { get; init; }
    public bool EnableSingleCallsiteInlining { get; init; }
    public IReadOnlyList<OptimizationDirective> OptimizationDirectives { get; init; } = [];
    public IReadOnlyDictionary<string, string> NamedModuleRoots { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public int ComptimeFuel { get; init; }

    public static CommandLineOptions? Parse(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return null;
        }

        string? filePath = null;
        string? dumpDirectory = null;
        bool dumpBound = false;
        bool dumpMirPreOptimization = false;
        bool dumpMir = false;
        bool dumpLirPreOptimization = false;
        bool dumpLir = false;
        bool dumpAsmirPreOptimization = false;
        bool dumpAsmir = false;
        bool dumpFinalAsm = false;
        bool dumpAll = false;
        bool json = false;
        string? outputPath = null;
        List<string> compilerArgs = [];

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--dump-bound":
                    dumpBound = true;
                    break;

                case "--dump-mir":
                    dumpMir = true;
                    break;

                case "--dump-mir-preopt":
                    dumpMirPreOptimization = true;
                    break;

                case "--dump-lir":
                    dumpLir = true;
                    break;

                case "--dump-lir-preopt":
                    dumpLirPreOptimization = true;
                    break;

                case "--dump-asmir":
                    dumpAsmir = true;
                    break;

                case "--dump-asmir-preopt":
                    dumpAsmirPreOptimization = true;
                    break;

                case "--dump-final-asm":
                    dumpFinalAsm = true;
                    break;

                case "--dump-all":
                    dumpAll = true;
                    break;

                case "--json":
                    json = true;
                    break;

                case string value when CompilationOptionsCommandLine.IsCompilationOption(value):
                    compilerArgs.Add(value);
                    break;

                case "--dump-dir":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("error: missing value for --dump-dir");
                        return null;
                    }

                    dumpDirectory = args[++i];
                    break;

                case "--output":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("error: missing value for --output");
                        return null;
                    }

                    outputPath = args[++i];
                    break;

                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"error: unknown option '{arg}'");
                        PrintUsage();
                        return null;
                    }

                    if (filePath is not null)
                    {
                        Console.Error.WriteLine("error: multiple input files are not supported.");
                        return null;
                    }

                    filePath = arg;
                    break;
            }
        }

        if (!CompilationOptionsCommandLine.TryParse(compilerArgs, Environment.CurrentDirectory, out CompilationOptions compilerOptions, out string? compilerError))
        {
            Console.Error.WriteLine(compilerError);
            return null;
        }

        if (filePath is null)
        {
            Console.Error.WriteLine("error: missing input file.");
            PrintUsage();
            return null;
        }

        if (dumpAll)
        {
            dumpBound = true;
            dumpMirPreOptimization = true;
            dumpMir = true;
            dumpLirPreOptimization = true;
            dumpLir = true;
            dumpAsmirPreOptimization = true;
            dumpAsmir = true;
            dumpFinalAsm = true;
        }

        if (dumpDirectory is not null && json)
        {
            Console.Error.WriteLine("error: --json cannot be combined with --dump-dir");
            return null;
        }

        if (dumpDirectory is not null && outputPath is not null)
        {
            Console.Error.WriteLine("error: --output cannot be combined with --dump-dir");
            return null;
        }

        return new CommandLineOptions
        {
            FilePath = filePath,
            DumpBound = dumpBound,
            DumpMirPreOptimization = dumpMirPreOptimization,
            DumpMir = dumpMir,
            DumpLirPreOptimization = dumpLirPreOptimization,
            DumpLir = dumpLir,
            DumpAsmirPreOptimization = dumpAsmirPreOptimization,
            DumpAsmir = dumpAsmir,
            DumpFinalAsm = dumpFinalAsm,
            DumpDirectory = dumpDirectory,
            Json = json,
            OutputPath = outputPath,
            EnableSingleCallsiteInlining = compilerOptions.EnableSingleCallsiteInlining,
            OptimizationDirectives = compilerOptions.OptimizationDirectives,
            NamedModuleRoots = compilerOptions.NamedModuleRoots,
            ComptimeFuel = compilerOptions.ComptimeFuel,
        };
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: blade <file.blade> [options]");
        Console.Error.WriteLine("Options:");
        Console.Error.WriteLine("  --dump-bound");
        Console.Error.WriteLine("  --dump-mir-preopt");
        Console.Error.WriteLine("  --dump-mir");
        Console.Error.WriteLine("  --dump-lir-preopt");
        Console.Error.WriteLine("  --dump-lir");
        Console.Error.WriteLine("  --dump-asmir-preopt");
        Console.Error.WriteLine("  --dump-asmir");
        Console.Error.WriteLine("  --dump-final-asm");
        Console.Error.WriteLine("  --dump-all");
        Console.Error.WriteLine("  --dump-dir <path>");
        Console.Error.WriteLine("  --json");
        Console.Error.WriteLine("  --output <file>");
        Console.Error.WriteLine("  -fmir-opt=<csv> / -fno-mir-opt=<csv>");
        Console.Error.WriteLine("  -flir-opt=<csv> / -fno-lir-opt=<csv>");
        Console.Error.WriteLine("  -fasmir-opt=<csv> / -fno-asmir-opt=<csv>");
        Console.Error.WriteLine("  --module=<name>=<path>");
    }
}

internal sealed class CompilationMetrics
{
    [JsonPropertyName("token_count")]
    public required int TokenCount { get; init; }

    [JsonPropertyName("member_count")]
    public required int MemberCount { get; init; }

    [JsonPropertyName("bound_function_count")]
    public required int BoundFunctionCount { get; init; }

    [JsonPropertyName("mir_function_count")]
    public required int MirFunctionCount { get; init; }

    [JsonPropertyName("time_ms")]
    public required double TimeMs { get; init; }
}

internal static class OutputWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static bool TryWriteText(
        CommandLineOptions options,
        IReadOnlyDictionary<string, string> dumpContent,
        CompilationMetrics metrics,
        int errorCount,
        out string? error)
    {
        try
        {
            if (options.DumpDirectory is not null)
            {
                Directory.CreateDirectory(options.DumpDirectory);
                foreach ((string fileName, string content) in dumpContent)
                {
                    string path = Path.Combine(options.DumpDirectory, fileName);
                    File.WriteAllText(path, content);
                }

                WriteTextReport(Console.Out, dumpContent, metrics, errorCount, includeDumps: false);
                error = null;
                return true;
            }

            if (options.OutputPath is null || options.OutputPath == "-")
            {
                WriteTextReport(Console.Out, dumpContent, metrics, errorCount, includeDumps: true);
                error = null;
                return true;
            }

            using StreamWriter writer = new(options.OutputPath);
            WriteTextReport(writer, dumpContent, metrics, errorCount, includeDumps: true);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string target = options.DumpDirectory ?? options.OutputPath ?? "stdout";
            error = $"error: failed to write output to '{target}': {ex.Message}";
            return false;
        }
    }

    public static bool TryWriteJson(
        CommandLineOptions options,
        JsonCompilationReport report,
        out string? error)
    {
        try
        {
            if (options.OutputPath is null || options.OutputPath == "-")
            {
                WriteJsonReport(Console.Out, report);
                error = null;
                return true;
            }

            using StreamWriter writer = new(options.OutputPath);
            WriteJsonReport(writer, report);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string target = options.OutputPath ?? "stdout";
            error = $"error: failed to write output to '{target}': {ex.Message}";
            return false;
        }
    }

    private static void WriteTextReport(
        TextWriter writer,
        IReadOnlyDictionary<string, string> dumpContent,
        CompilationMetrics metrics,
        int errorCount,
        bool includeDumps)
    {
        if (includeDumps)
        {
            bool first = true;
            foreach ((string fileName, string content) in dumpContent)
            {
                if (!first)
                    writer.WriteLine();
                first = false;
                writer.WriteLine($"' {fileName}");
                writer.WriteLine(content);
            }

            if (dumpContent.Count > 0)
                writer.WriteLine();
        }

        writer.WriteLine($"tokens : {metrics.TokenCount}");
        writer.WriteLine($"members: {metrics.MemberCount}");
        writer.WriteLine($"bound-fns: {metrics.BoundFunctionCount}");
        writer.WriteLine($"mir-fns: {metrics.MirFunctionCount}");
        writer.WriteLine($"errors : {errorCount}");
        writer.WriteLine($"time   : {metrics.TimeMs:F2} ms");
    }

    private static void WriteJsonReport(TextWriter writer, JsonCompilationReport report)
    {
        writer.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
    }
}

internal static class JsonReportBuilder
{
    public static JsonCompilationReport Build(
        CompilationResult compilation,
        CommandLineOptions options,
        CompilationMetrics metrics)
    {
        bool success = compilation.Diagnostics.Count == 0 && compilation.IrBuildResult is not null;
        Dictionary<string, string?> dumps = BuildJsonDumps(compilation, options, success);
        List<JsonDiagnostic> diagnostics = [];
        foreach (Diagnostic diagnostic in compilation.Diagnostics)
        {
            SourceLocation location = compilation.Source.GetLocation(diagnostic.Span.Start);
            diagnostics.Add(new JsonDiagnostic
            {
                File = location.FilePath,
                Line = location.Line,
                Code = diagnostic.FormatCode(),
                Message = diagnostic.Message,
            });
        }

        return new JsonCompilationReport
        {
            Success = success,
            Diagnostics = diagnostics,
            Dumps = dumps,
            Result = success ? compilation.IrBuildResult!.AssemblyText : null,
            Metrics = metrics,
        };
    }

    private static Dictionary<string, string?> BuildJsonDumps(
        CompilationResult compilation,
        CommandLineOptions options,
        bool success)
    {
        Dictionary<string, string?> dumps = new()
        {
            ["bound"] = null,
            ["mir-preopt"] = null,
            ["mir"] = null,
            ["lir-preopt"] = null,
            ["lir"] = null,
            ["asmir-preopt"] = null,
            ["asmir"] = null,
        };

        if (!success)
            return dumps;

        IrBuildResult buildResult = compilation.IrBuildResult!;
        if (options.DumpBound)
            dumps["bound"] = Blade.Semantics.Bound.BoundTreeWriter.Write(buildResult.BoundProgram);
        if (options.DumpMirPreOptimization)
            dumps["mir-preopt"] = Blade.IR.Mir.MirTextWriter.Write(buildResult.PreOptimizationMirModule);
        if (options.DumpMir)
            dumps["mir"] = Blade.IR.Mir.MirTextWriter.Write(buildResult.MirModule);
        if (options.DumpLirPreOptimization)
            dumps["lir-preopt"] = Blade.IR.Lir.LirTextWriter.Write(buildResult.PreOptimizationLirModule);
        if (options.DumpLir)
            dumps["lir"] = Blade.IR.Lir.LirTextWriter.Write(buildResult.LirModule);
        if (options.DumpAsmirPreOptimization)
            dumps["asmir-preopt"] = Blade.IR.Asm.AsmTextWriter.Write(buildResult.PreOptimizationAsmModule);
        if (options.DumpAsmir)
            dumps["asmir"] = Blade.IR.Asm.AsmTextWriter.Write(buildResult.AsmModule);
        return dumps;
    }
}

internal sealed class JsonCompilationReport
{
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("diagnostics")]
    public required IReadOnlyList<JsonDiagnostic> Diagnostics { get; init; }

    [JsonPropertyName("dumps")]
    public required IReadOnlyDictionary<string, string?> Dumps { get; init; }

    [JsonPropertyName("result")]
    public required string? Result { get; init; }

    [JsonPropertyName("metrics")]
    public required CompilationMetrics Metrics { get; init; }
}

internal sealed class JsonDiagnostic
{
    [JsonPropertyName("file")]
    public required string File { get; init; }

    [JsonPropertyName("line")]
    public required int Line { get; init; }

    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}
