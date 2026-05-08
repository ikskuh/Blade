using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blade.Reports;

/// <summary>
/// Renders compilation output as JSON.
/// </summary>
public sealed class JsonReportWriter : IReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Writes the supplied compilation output as JSON.
    /// </summary>
    public void Write(TextWriter writer, CompilationOutput report)
    {
        Requires.NotNull(writer);
        Requires.NotNull(report);

        JsonCompilationOutput json = new()
        {
            Status = FormatStatus(report.Status),
            SourceFile = report.Source.FilePath,
            Diagnostics = report.Diagnostics.Select(static diagnostic => new JsonDiagnostic
            {
                File = diagnostic.IsLocated ? diagnostic.GetLocation().FilePath : null,
                Line = diagnostic.IsLocated ? diagnostic.GetLocation().Line : null,
                Code = diagnostic.FormatCode(),
                Message = diagnostic.Message,
            }).ToList(),
            Metrics = report.Metrics,
            Crash = report.Crash is null
                ? null
                : new JsonCrashInfo
                {
                    ExceptionType = report.Crash.ExceptionType,
                    Message = report.Crash.Message,
                    StackTrace = report.Crash.StackTrace,
                },
            Sections = ReportSectionCatalog.BuildSections(report).Select(static section => new JsonReportSection
            {
                Id = section.Id,
                Title = section.Title,
                FileName = section.FileName,
                Content = section.RenderPlainText(),
            }).ToList(),
        };
        writer.WriteLine(JsonSerializer.Serialize(json, JsonOptions));
    }

    private static string FormatStatus(CompilationStatus status)
    {
        return status switch
        {
            CompilationStatus.Succeeded => "succeeded",
            CompilationStatus.Failed => "failed",
            CompilationStatus.Crashed => "crashed",
            _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
        };
    }
}

internal sealed class JsonCompilationOutput
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("source_file")]
    public required string SourceFile { get; init; }

    [JsonPropertyName("diagnostics")]
    public required System.Collections.Generic.IReadOnlyList<JsonDiagnostic> Diagnostics { get; init; }

    [JsonPropertyName("metrics")]
    public required CompilationMetrics Metrics { get; init; }

    [JsonPropertyName("crash")]
    public required JsonCrashInfo? Crash { get; init; }

    [JsonPropertyName("sections")]
    public required System.Collections.Generic.IReadOnlyList<JsonReportSection> Sections { get; init; }
}

internal sealed class JsonDiagnostic
{
    [JsonPropertyName("file")]
    public required string? File { get; init; }

    [JsonPropertyName("line")]
    public required int? Line { get; init; }

    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

internal sealed class JsonCrashInfo
{
    [JsonPropertyName("exception_type")]
    public required string ExceptionType { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("stack_trace")]
    public required string? StackTrace { get; init; }
}

internal sealed class JsonReportSection
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("file_name")]
    public required string FileName { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }
}
