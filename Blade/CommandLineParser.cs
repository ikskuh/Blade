using System;
using System.Collections.Generic;

namespace Blade;

internal static class CommandLineParser
{
    public static CommandLineOptions? Parse(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return null;
        }

        string? filePath = null;
        List<ReportTarget> reportTargets = [];
        List<string> compilerArgs = [];

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case string value when CompilationOptionsCommandLine.IsCompilationOption(value):
                    compilerArgs.Add(value);
                    break;

                case "--report":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("error: missing value for --report");
                        return null;
                    }

                    if (!TryParseReportTarget(args[++i], out ReportTarget? reportTarget, out string? reportError))
                    {
                        Console.Error.WriteLine(reportError);
                        return null;
                    }

                    reportTargets.Add(reportTarget!);
                    break;

                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        if (IsRemovedReportOption(arg))
                        {
                            Console.Error.WriteLine($"error: option '{arg}' has been removed; use --report <text|html|json>,<path> instead.");
                            return null;
                        }

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

        int stdoutReportCount = 0;
        foreach (ReportTarget reportTarget in reportTargets)
        {
            if (reportTarget.Path == "-")
                stdoutReportCount++;
        }

        if (stdoutReportCount > 1)
        {
            Console.Error.WriteLine("error: only one --report target may write to stdout ('-').");
            return null;
        }

        return new CommandLineOptions
        {
            FilePath = filePath,
            ReportTargets = reportTargets,
            EnableSingleCallsiteInlining = compilerOptions.EnableSingleCallsiteInlining,
            EnabledMirOptimizations = compilerOptions.EnabledMirOptimizations,
            EnabledLirOptimizations = compilerOptions.EnabledLirOptimizations,
            EnabledAsmirOptimizations = compilerOptions.EnabledAsmirOptimizations,
            NamedModuleRoots = compilerOptions.NamedModuleRoots,
            ComptimeFuel = compilerOptions.ComptimeFuel,
            RuntimeLauncherPath = compilerOptions.RuntimeLauncherPath,
        };
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: blade <file.blade> [options]");
        Console.Error.WriteLine("Options:");
        Console.Error.WriteLine("  --report <text|html|json>,<path>");
        Console.Error.WriteLine("  --comptime-fuel=<positive-integer>");
        Console.Error.WriteLine("  -fmir-opt=<csv> / -fno-mir-opt=<csv>");
        Console.Error.WriteLine("  -flir-opt=<csv> / -fno-lir-opt=<csv>");
        Console.Error.WriteLine("  -fasmir-opt=<csv> / -fno-asmir-opt=<csv>");
        Console.Error.WriteLine("  --module=<name>=<path>");
        Console.Error.WriteLine("  --runtime=<path>");
    }

    private static bool IsRemovedReportOption(string arg)
    {
        return arg is "--dump-bound"
            or "--dump-mir-preopt"
            or "--dump-mir"
            or "--dump-lir-preopt"
            or "--dump-lir"
            or "--dump-asmir-preopt"
            or "--dump-asmir"
            or "--dump-mmap"
            or "--dump-final-asm"
            or "--dump-all"
            or "--dump-dir"
            or "--output"
            or "--json"
            or "--metrics";
    }

    private static bool TryParseReportTarget(string value, out ReportTarget? reportTarget, out string? error)
    {
        Requires.NotNull(value);

        int separatorIndex = value.IndexOf(',', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex == value.Length - 1)
        {
            reportTarget = null;
            error = $"error: invalid --report target '{value}'; expected <text|html|json>,<path>.";
            return false;
        }

        string formatText = value[..separatorIndex];
        string path = value[(separatorIndex + 1)..];
        if (path.Length == 0)
        {
            reportTarget = null;
            error = $"error: invalid --report target '{value}'; missing output path.";
            return false;
        }

        ReportFormat format;
        switch (formatText)
        {
            case "text":
                format = ReportFormat.Text;
                break;
            case "html":
                format = ReportFormat.Html;
                break;
            case "json":
                format = ReportFormat.Json;
                break;
            default:
                reportTarget = null;
                error = $"error: unknown report format '{formatText}'.";
                return false;
        }

        reportTarget = new ReportTarget(format, path);
        error = null;
        return true;
    }
}
