using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Blade;
using Blade.Diagnostics;
using Blade.IR;
using Blade.Source;

CommandLineOptions? options = CommandLineOptions.Parse(args);
if (options is null)
    return 1;

if (!File.Exists(options.FilePath))
{
    Console.Error.WriteLine($"error: file not found: {options.FilePath}");
    return 1;
}

string text = File.ReadAllText(options.FilePath);
Stopwatch sw = Stopwatch.StartNew();
CompilationResult compilation = CompilerDriver.Compile(
    text,
    options.FilePath,
    new CompilationOptions
    {
        EnableSingleCallsiteInlining = options.EnableSingleCallsiteInlining,
        OptimizationDirectives = options.OptimizationDirectives,
        NamedModuleRoots = options.NamedModuleRoots,
        ComptimeFuel = options.ComptimeFuel,
    });
sw.Stop();

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
if (options.DumpDirectory is not null)
{
    Directory.CreateDirectory(options.DumpDirectory);
    foreach ((string fileName, string content) in dumpContent)
    {
        string path = Path.Combine(options.DumpDirectory, fileName);
        File.WriteAllText(path, content);
    }
}
else
{
    bool first = true;
    foreach ((string fileName, string content) in dumpContent)
    {
        if (!first)
            Console.WriteLine();
        first = false;
        Console.WriteLine($"' {fileName}");
        Console.WriteLine(content);
    }
}

Console.WriteLine();
Console.WriteLine($"tokens : {compilation.TokenCount}");
Console.WriteLine($"members: {compilation.Syntax.Members.Count}");
Console.WriteLine($"bound-fns: {compilation.BoundProgram.Functions.Count}");
Console.WriteLine($"mir-fns: {compilation.IrBuildResult.MirModule.Functions.Count}");
Console.WriteLine($"errors : {compilation.Diagnostics.Count}");
Console.WriteLine($"time   : {sw.Elapsed.TotalMilliseconds:F2} ms");

return 0;

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
        Console.Error.WriteLine("  -fmir-opt=<csv> / -fno-mir-opt=<csv>");
        Console.Error.WriteLine("  -flir-opt=<csv> / -fno-lir-opt=<csv>");
        Console.Error.WriteLine("  -fasmir-opt=<csv> / -fno-asmir-opt=<csv>");
        Console.Error.WriteLine("  --module=<name>=<path>");
    }
}
