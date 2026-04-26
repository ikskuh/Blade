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
        bool success = !HasErrors(compilation.Diagnostics) && compilation.IrBuildResult is not null;
        IReadOnlyList<DumpArtifact> dumps = BuildJsonDumps(compilation, options, success);
        List<JsonDiagnostic> diagnostics = [];
        foreach (Diagnostic diagnostic in compilation.Diagnostics)
        {
            SourceLocation location = diagnostic.GetLocation();
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
            Metrics = options.EmitMetrics ? metrics : null,
        };
    }

    private static bool HasErrors(IReadOnlyList<Diagnostic> diagnostics)
    {
        foreach (Diagnostic diagnostic in diagnostics)
        {
            if (diagnostic.IsError)
                return true;
        }

        return false;
    }

    private static IReadOnlyList<DumpArtifact> BuildJsonDumps(
        CompilationResult compilation,
        CommandLineOptions options,
        bool success)
    {
        if (!success)
            return [];

        IrBuildResult buildResult = compilation.IrBuildResult!;
        DumpSelection dumpSelection = DumpSelectionFactory.FromCommandLineOptions(options);
        return DumpBundleBuilder.Build(dumpSelection, buildResult);
    }
}

internal sealed class JsonCompilationReport
{
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("diagnostics")]
    public required IReadOnlyList<JsonDiagnostic> Diagnostics { get; init; }

    [JsonPropertyName("dumps")]
    public required IReadOnlyList<DumpArtifact> Dumps { get; init; }

    [JsonPropertyName("result")]
    public required string? Result { get; init; }

    [JsonPropertyName("metrics")]
    public required CompilationMetrics? Metrics { get; init; }
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
