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
        string? dumpDirectory = null;
        bool dumpBound = false;
        bool dumpMirPreOptimization = false;
        bool dumpMir = false;
        bool dumpLirPreOptimization = false;
        bool dumpLir = false;
        bool dumpAsmirPreOptimization = false;
        bool dumpAsmir = false;
        bool dumpMemoryMap = false;
        bool dumpFinalAsm = false;
        bool dumpAll = false;
        bool json = false;
        bool emitMetrics = false;
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

                case "--dump-mmap":
                    dumpMemoryMap = true;
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

                case "--metrics":
                    emitMetrics = true;
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
            dumpMemoryMap = true;
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
            DumpMemoryMap = dumpMemoryMap,
            DumpFinalAsm = dumpFinalAsm,
            DumpDirectory = dumpDirectory,
            Json = json,
            EmitMetrics = emitMetrics,
            OutputPath = outputPath,
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
        Console.Error.WriteLine("  --dump-bound");
        Console.Error.WriteLine("  --dump-mir-preopt");
        Console.Error.WriteLine("  --dump-mir");
        Console.Error.WriteLine("  --dump-lir-preopt");
        Console.Error.WriteLine("  --dump-lir");
        Console.Error.WriteLine("  --dump-asmir-preopt");
        Console.Error.WriteLine("  --dump-asmir");
        Console.Error.WriteLine("  --dump-mmap");
        Console.Error.WriteLine("  --dump-final-asm");
        Console.Error.WriteLine("  --dump-all");
        Console.Error.WriteLine("  --dump-dir <path>");
        Console.Error.WriteLine("  --json");
        Console.Error.WriteLine("  --metrics");
        Console.Error.WriteLine("  --output <file>");
        Console.Error.WriteLine("  --comptime-fuel=<positive-integer>");
        Console.Error.WriteLine("  -fmir-opt=<csv> / -fno-mir-opt=<csv>");
        Console.Error.WriteLine("  -flir-opt=<csv> / -fno-lir-opt=<csv>");
        Console.Error.WriteLine("  -fasmir-opt=<csv> / -fno-asmir-opt=<csv>");
        Console.Error.WriteLine("  --module=<name>=<path>");
        Console.Error.WriteLine("  --runtime=<path>");
    }
}
