using System;
using System.Collections.Generic;
using System.IO;

namespace Blade.Reports;

internal sealed class ReportOutputWriter : ICompilerOutputWriter
{
    private static readonly IReadOnlyDictionary<string, string> EmptyProperties = new Dictionary<string, string>(StringComparer.Ordinal);

    public bool TryWrite(
        CommandLineOptions options,
        CompilationOutput compilation,
        out int exitCode,
        out string? error)
    {
        Requires.NotNull(options);
        Requires.NotNull(compilation);

        try
        {
            if (options.ReportTargets.Count == 0)
            {
                WriteDefaultOutput(compilation);
            }
            else
            {
                foreach (ReportTarget target in options.ReportTargets)
                    WriteTarget(compilation, target);
            }

            exitCode = compilation.Status == CompilationStatus.Succeeded ? 0 : 1;
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error = $"error: failed to write report output: {ex.Message}";
            exitCode = 1;
            return false;
        }
    }

    private static void WriteDefaultOutput(CompilationOutput compilation)
    {
        IReportWriter writer = compilation.Status == CompilationStatus.Succeeded && compilation.Stages.IsComplete
            ? ReportWriterFactory.CreateWriter(ReportFormat.Assembly)
            : ReportWriterFactory.CreateWriter(ReportFormat.Text);
        writer.Write(Console.Out, compilation, EmptyProperties);
    }

    private static void WriteTarget(CompilationOutput compilation, ReportTarget target)
    {
        if (target.Path == "-")
        {
            target.Writer.Write(Console.Out, compilation, target.Properties);
            return;
        }

        using StreamWriter streamWriter = new(target.Path);
        target.Writer.Write(streamWriter, compilation, target.Properties);
    }
}
