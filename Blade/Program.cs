using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Blade.IR;
using Blade.IR.Asm;
using Blade.IR.Mir;

namespace Blade;

internal static class Program
{
    public static int Main(string[] args)
    {
        CommandLineOptions? options = CommandLineParser.Parse(args);
        if (options is null)
            return 1;

        if (options.FilePath != "-" && !File.Exists(options.FilePath))
        {
            Console.Error.WriteLine($"error: file not found: {options.FilePath}");
            return 1;
        }


        Stopwatch sw = Stopwatch.StartNew();

        var bladeOptions = new CompilationOptions
        {
            EnableSingleCallsiteInlining = options.EnableSingleCallsiteInlining,
            EnabledMirOptimizations = options.EnabledMirOptimizations,
            EnabledLirOptimizations = options.EnabledLirOptimizations,
            EnabledAsmirOptimizations = options.EnabledAsmirOptimizations,
            NamedModuleRoots = options.NamedModuleRoots,
            ComptimeFuel = options.ComptimeFuel,
            RuntimeLauncherPath = options.RuntimeLauncherPath,
        };

        CompilationResult compilation;
        if (options.FilePath == "-")
        {
            var stdinText = Console.In.ReadToEnd();
            compilation = CompilerDriver.Compile(stdinText, "<stdin>", bladeOptions);
        }
        else
        {
            compilation = CompilerDriver.CompileFile(options.FilePath, bladeOptions);
        }
        sw.Stop();

        CompilationMetrics metrics = new()
        {
            TokenCount = compilation.TokenCount,
            MemberCount = compilation.Syntax.Members.Count,
            BoundFunctionCount = compilation.BoundProgram?.Functions.Count ?? 0,
            MirFunctionCount = compilation.IrBuildResult?.MirModules.Sum(static module => module.Functions.Count) ?? 0,
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
