using System;
using System.Collections.Generic;
using System.IO;
using Blade.Diagnostics;
using Blade.IR;
using Blade.Source;

namespace Blade;

internal sealed class StdioOutputWriter : ICompilerOutputWriter
{
    public bool TryWrite(
        CommandLineOptions options,
        CompilationResult compilation,
        CompilationMetrics metrics,
        out int exitCode,
        out string? error)
    {
        int errorCount = CountErrors(compilation.Diagnostics);

        foreach (Diagnostic diagnostic in compilation.Diagnostics)
        {
            SourceLocation location = compilation.Source.GetLocation(diagnostic.Span.Start);
            Console.WriteLine($"{location}: {diagnostic}");
        }

        if (errorCount > 0)
        {
            exitCode = 1;
            error = null;
            return true;
        }

        Assert.Invariant(compilation.IrBuildResult is not null, "Successful text output requires an IR build result.");

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

        bool writeSucceeded = TryWriteText(
            options,
            dumpContent,
            metrics,
            errorCount,
            out error);
        exitCode = writeSucceeded ? 0 : 1;
        return writeSucceeded;
    }

    private static int CountErrors(IReadOnlyList<Diagnostic> diagnostics)
    {
        int count = 0;
        foreach (Diagnostic diagnostic in diagnostics)
        {
            if (diagnostic.IsError)
                count++;
        }

        return count;
    }

    private static bool TryWriteText(
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

                if (options.EmitMetrics)
                    WriteTextReport(Console.Out, dumpContent, metrics, errorCount, includeDumps: false);
                error = null;
                return true;
            }

            if (options.OutputPath is null || options.OutputPath == "-")
            {
                WriteTextReport(Console.Out, dumpContent, metrics, errorCount, includeDumps: true, includeMetrics: options.EmitMetrics);
                error = null;
                return true;
            }

            using StreamWriter writer = new(options.OutputPath);
            WriteTextReport(writer, dumpContent, metrics, errorCount, includeDumps: true, includeMetrics: options.EmitMetrics);
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

    private static void WriteTextReport(
        TextWriter writer,
        IReadOnlyDictionary<string, string> dumpContent,
        CompilationMetrics metrics,
        int errorCount,
        bool includeDumps,
        bool includeMetrics = true)
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

        if (!includeMetrics)
            return;

        writer.WriteLine($"' tokens : {metrics.TokenCount}");
        writer.WriteLine($"' members: {metrics.MemberCount}");
        writer.WriteLine($"' bound-fns: {metrics.BoundFunctionCount}");
        writer.WriteLine($"' mir-fns: {metrics.MirFunctionCount}");
        writer.WriteLine($"' errors : {errorCount}");
        writer.WriteLine($"' time   : {metrics.TimeMs:F2} ms");
    }
}
