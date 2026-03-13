using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Blade.Diagnostics;
using Blade.IR;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;
using Blade.Syntax;
using Blade.Syntax.Nodes;

CommandLineOptions? options = CommandLineOptions.Parse(args);
if (options is null)
    return 1;

if (!File.Exists(options.FilePath))
{
    Console.Error.WriteLine($"error: file not found: {options.FilePath}");
    return 1;
}

string text = File.ReadAllText(options.FilePath);
SourceText source = new(text, options.FilePath);
DiagnosticBag diagnostics = new();

Stopwatch sw = Stopwatch.StartNew();
Parser parser = Parser.Create(source, diagnostics);
CompilationUnitSyntax unit = parser.ParseCompilationUnit();
BoundProgram boundProgram = Binder.Bind(unit, diagnostics);
IrBuildResult? irBuild = null;
if (diagnostics.Count == 0)
{
    IrPipelineOptions pipelineOptions = new()
    {
        EnableSingleCallsiteInlining = options.EnableSingleCallsiteInlining,
    };
    irBuild = IrPipeline.Build(boundProgram, pipelineOptions);
}
sw.Stop();

foreach (Diagnostic diag in diagnostics)
{
    SourceLocation loc = source.GetLocation(diag.Span.Start);
    Console.WriteLine($"{loc}: {diag}");
}

if (diagnostics.Count > 0)
    return 1;

if (irBuild is null)
    return 1;

Dictionary<string, string> dumpContent = BuildDumpContent(options, irBuild);
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
        Console.WriteLine($";; {fileName}");
        Console.WriteLine(content);
    }
}

Console.WriteLine();
Console.WriteLine($"tokens : {parser.TokenCount}");
Console.WriteLine($"members: {unit.Members.Count}");
Console.WriteLine($"bound-fns: {boundProgram.Functions.Count}");
Console.WriteLine($"mir-fns: {irBuild.MirModule.Functions.Count}");
Console.WriteLine($"errors : {diagnostics.Count}");
Console.WriteLine($"time   : {sw.Elapsed.TotalMilliseconds:F2} ms");

return 0;

static Dictionary<string, string> BuildDumpContent(CommandLineOptions options, IrBuildResult buildResult)
{
    Dictionary<string, string> dumps = [];
    if (!options.DumpBound
        && !options.DumpMir
        && !options.DumpLir
        && !options.DumpAsmir
        && !options.DumpFinalAsm)
    {
        dumps["40_final.spin2"] = buildResult.AssemblyText;
        return dumps;
    }

    if (options.DumpBound)
        dumps["00_bound.ir"] = BoundTreeWriter.Write(buildResult.BoundProgram);
    if (options.DumpMir)
        dumps["10_mir.ir"] = MirTextWriter.Write(buildResult.MirModule);
    if (options.DumpLir)
        dumps["20_lir.ir"] = LirTextWriter.Write(buildResult.LirModule);
    if (options.DumpAsmir)
        dumps["30_asmir.ir"] = AsmTextWriter.Write(buildResult.AsmModule);
    if (options.DumpFinalAsm)
        dumps["40_final.spin2"] = buildResult.AssemblyText;
    return dumps;
}

internal sealed class CommandLineOptions
{
    private CommandLineOptions()
    {
    }

    public required string FilePath { get; init; }
    public bool DumpBound { get; init; }
    public bool DumpMir { get; init; }
    public bool DumpLir { get; init; }
    public bool DumpAsmir { get; init; }
    public bool DumpFinalAsm { get; init; }
    public string? DumpDirectory { get; init; }
    public bool EnableSingleCallsiteInlining { get; init; }

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
        bool dumpMir = false;
        bool dumpLir = false;
        bool dumpAsmir = false;
        bool dumpFinalAsm = false;
        bool dumpAll = false;
        bool enableSingleCallsiteInlining = true;

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

                case "--dump-lir":
                    dumpLir = true;
                    break;

                case "--dump-asmir":
                    dumpAsmir = true;
                    break;

                case "--dump-final-asm":
                    dumpFinalAsm = true;
                    break;

                case "--dump-all":
                    dumpAll = true;
                    break;

                case "--no-single-callsite-inline":
                    enableSingleCallsiteInlining = false;
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

        if (filePath is null)
        {
            Console.Error.WriteLine("error: missing input file.");
            PrintUsage();
            return null;
        }

        if (dumpAll)
        {
            dumpBound = true;
            dumpMir = true;
            dumpLir = true;
            dumpAsmir = true;
            dumpFinalAsm = true;
        }

        return new CommandLineOptions
        {
            FilePath = filePath,
            DumpBound = dumpBound,
            DumpMir = dumpMir,
            DumpLir = dumpLir,
            DumpAsmir = dumpAsmir,
            DumpFinalAsm = dumpFinalAsm,
            DumpDirectory = dumpDirectory,
            EnableSingleCallsiteInlining = enableSingleCallsiteInlining,
        };
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: blade <file.blade> [options]");
        Console.Error.WriteLine("Options:");
        Console.Error.WriteLine("  --dump-bound");
        Console.Error.WriteLine("  --dump-mir");
        Console.Error.WriteLine("  --dump-lir");
        Console.Error.WriteLine("  --dump-asmir");
        Console.Error.WriteLine("  --dump-final-asm");
        Console.Error.WriteLine("  --dump-all");
        Console.Error.WriteLine("  --dump-dir <path>");
        Console.Error.WriteLine("  --no-single-callsite-inline");
    }
}
