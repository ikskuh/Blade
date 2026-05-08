using System;
using System.Collections.Generic;
using System.IO;
using Blade.Reports;

namespace Blade;

internal sealed class ReportOutputWriter : ICompilerOutputWriter
{
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"error: failed to write report output: {ex.Message}";
            exitCode = 1;
            return false;
        }
    }

    private static void WriteDefaultOutput(CompilationOutput compilation)
    {
        IReportWriter writer = compilation.Status == CompilationStatus.Succeeded && compilation.Stages.IsComplete
            ? new TextReportWriter(bareFinalAssemblyOnly: true)
            : new TextReportWriter(bareFinalAssemblyOnly: false);
        writer.Write(Console.Out, compilation);
    }

    private static void WriteTarget(CompilationOutput compilation, ReportTarget target)
    {
        IReportWriter writer = CreateWriter(target.Format);
        if (target.Path == "-")
        {
            writer.Write(Console.Out, compilation);
            return;
        }

        using StreamWriter streamWriter = new(target.Path);
        writer.Write(streamWriter, compilation);
    }

    private static IReportWriter CreateWriter(ReportFormat format)
    {
        return format switch
        {
            ReportFormat.Text => new TextReportWriter(bareFinalAssemblyOnly: false),
            ReportFormat.Html => new HtmlReportWriter(),
            ReportFormat.Json => new JsonReportWriter(),
            _ => Assert.UnreachableValue<IReportWriter>(), // pragma: force-coverage
        };
    }
}
