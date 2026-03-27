using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Blade.IR;
using Blade.IR.Asm;

namespace Blade;

internal static class Program
{
    public static int Main(string[] args)
    {
        CommandLineOptions? options = CommandLineParser.Parse(args);
        if (options is null)
            return 1;

        if (!File.Exists(options.FilePath))
        {
            Console.Error.WriteLine($"error: file not found: {options.FilePath}");
            return 1;
        }

        // TODO: Remove this line when the Method Usage Analyzer is fixed:
        Func<IReadOnlyList<AsmNode>, HashSet<string>> method =  AsmOptimizationHelpers.CollectJumpTargets;

        Stopwatch sw = Stopwatch.StartNew();
        CompilationResult compilation = CompilerDriver.CompileFile(
            options.FilePath,
            new CompilationOptions
            {
                EnableSingleCallsiteInlining = options.EnableSingleCallsiteInlining,
                EnabledMirOptimizations = options.EnabledMirOptimizations,
                EnabledLirOptimizations = options.EnabledLirOptimizations,
                EnabledAsmirOptimizations = options.EnabledAsmirOptimizations,
                NamedModuleRoots = options.NamedModuleRoots,
                ComptimeFuel = options.ComptimeFuel,
            });
        sw.Stop();

        CompilationMetrics metrics = new()
        {
            TokenCount = compilation.TokenCount,
            MemberCount = compilation.Syntax.Members.Count,
            BoundFunctionCount = compilation.BoundProgram.Functions.Count,
            MirFunctionCount = compilation.IrBuildResult?.MirModule.Functions.Count ?? 0,
            TimeMs = sw.Elapsed.TotalMilliseconds,
        };

        ICompilerOutputWriter outputWriter = options.Json
            ? new JsonOutputWriter()
            : new StdioOutputWriter();
        if (!outputWriter.TryWrite(options, compilation, metrics, out int exitCode, out string? outputError))
        {
            Console.Error.WriteLine(outputError);
            return 1;
        }

        return exitCode;
    }
}
