using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blade.Diagnostics;
using Blade.IR;
using Blade.Source;

namespace Blade;

internal sealed class JsonOutputWriter : ICompilerOutputWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public bool TryWrite(
        CommandLineOptions options,
        CompilationResult compilation,
        CompilationMetrics metrics,
        out int exitCode,
        out string? error)
    {
        JsonCompilationReport report = JsonReportBuilder.Build(compilation, options, metrics);
        bool writeSucceeded = TryWriteJson(options, report, out error);
        exitCode = writeSucceeded && report.Success ? 0 : 1;
        return writeSucceeded;
    }

    private static bool TryWriteJson(
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
