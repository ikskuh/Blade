using System.IO;
using Blade.Diagnostics;
using Blade.Source;

namespace Blade.Reports;

/// <summary>
/// Renders compilation output as plain text.
/// </summary>
public sealed class TextReportWriter : IReportWriter
{
    private readonly bool _bareFinalAssemblyOnly;

    /// <summary>
    /// Initializes a text report writer.
    /// </summary>
    public TextReportWriter(bool bareFinalAssemblyOnly)
    {
        _bareFinalAssemblyOnly = bareFinalAssemblyOnly;
    }

    /// <summary>
    /// Writes the supplied compilation output as plain text.
    /// </summary>
    public void Write(TextWriter writer, CompilationOutput report)
    {
        Requires.NotNull(writer);
        Requires.NotNull(report);

        if (_bareFinalAssemblyOnly && report.Status == CompilationStatus.Succeeded && report.Stages.AssemblyText is not null)
        {
            writer.Write(report.Stages.AssemblyText);
            return;
        }

        WriteHeader(writer, report);
        WriteDiagnostics(writer, report.Diagnostics);

        if (report.Crash is not null)
            WriteCrash(writer, report.Crash);

        foreach (ReportSection section in ReportSectionCatalog.BuildSections(report))
        {
            writer.WriteLine($"' {section.FileName}");
            writer.WriteLine(section.RenderPlainText());
            writer.WriteLine();
        }

        WriteMetrics(writer, report.Metrics);
    }

    private static void WriteHeader(TextWriter writer, CompilationOutput report)
    {
        writer.WriteLine($"status: {FormatStatus(report.Status)}");
        writer.WriteLine($"source: {report.Source.FilePath}");
        writer.WriteLine();
    }

    private static void WriteDiagnostics(TextWriter writer, System.Collections.Generic.IReadOnlyList<Diagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            writer.WriteLine("diagnostics: none");
            writer.WriteLine();
            return;
        }

        writer.WriteLine("diagnostics:");
        foreach (Diagnostic diagnostic in diagnostics)
        {
            if (diagnostic.IsLocated)
            {
                SourceLocation location = diagnostic.GetLocation();
                writer.Write("  ");
                writer.Write(location);
                writer.Write(": ");
                writer.WriteLine(diagnostic);
            }
            else
            {
                writer.Write("  ");
                writer.WriteLine(diagnostic);
            }
        }

        writer.WriteLine();
    }

    private static void WriteCrash(TextWriter writer, CompilationCrashInfo crash)
    {
        writer.WriteLine("crash:");
        writer.WriteLine($"  type: {crash.ExceptionType}");
        writer.WriteLine($"  message: {crash.Message}");
        if (!string.IsNullOrEmpty(crash.StackTrace))
        {
            writer.WriteLine("  stack:");
            foreach (string line in crash.StackTrace.Split('\n'))
                writer.WriteLine($"    {line.TrimEnd('\r')}");
        }

        writer.WriteLine();
    }

    private static void WriteMetrics(TextWriter writer, CompilationMetrics metrics)
    {
        writer.WriteLine("metrics:");
        writer.WriteLine($"  tokens: {metrics.TokenCount}");
        writer.WriteLine($"  members: {metrics.MemberCount}");
        writer.WriteLine($"  bound-fns: {metrics.BoundFunctionCount}");
        writer.WriteLine($"  mir-fns: {metrics.MirFunctionCount}");
        writer.WriteLine($"  time-ms: {metrics.TimeMs:F2}");
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
