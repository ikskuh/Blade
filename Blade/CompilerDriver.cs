using System;
using System.Collections.Generic;
using System.Linq;
using Blade.Diagnostics;
using Blade.IR;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;
using Blade.Syntax;
using Blade.Syntax.Nodes;

namespace Blade;

public sealed class CompilationOptions
{
    public bool EnableSingleCallsiteInlining { get; init; } = true;
    public bool EmitIr { get; init; } = true;
    public IReadOnlyList<MirOptimization> EnabledMirOptimizations { get; init; } = OptimizationRegistry.AllMirOptimizations;
    public IReadOnlyList<LirOptimization> EnabledLirOptimizations { get; init; } = OptimizationRegistry.AllLirOptimizations;
    public IReadOnlyList<AsmOptimization> EnabledAsmirOptimizations { get; init; } = OptimizationRegistry.AllAsmOptimizations;
    public IReadOnlyDictionary<string, string> NamedModuleRoots { get; init; } = new Dictionary<string, string>();
    public int ComptimeFuel { get; init; } = 250;
    /// <summary>
    /// Gets the optional Blade source file that overrides the default launcher task for the entry image.
    /// </summary>
    public string? RuntimeLauncherPath { get; init; }
}

public static class CompilerDriver
{
    /// <summary>
    /// Compiles one Blade source file into a structured compilation output.
    /// </summary>
    public static CompilationOutput CompileFile(string filePath, CompilationOptions? options = null)
    {
        DiagnosticBag diagnostics = new();
        try
        {
            bool sourceIsValid = SourceFileLoader.TryLoad(filePath, diagnostics, out SourceText source);
            if (!sourceIsValid)
                return CreateFailedCompilationOutput(source, diagnostics);

            return CompileCore(source, diagnostics, options ?? new CompilationOptions());
        }
        catch (Exception ex) when (IsRecoverableCompilerException(ex))
        {
            SourceText source = new(string.Empty, filePath);
            return CreateCrashedCompilationOutput(
                source,
                new CompilationUnitSyntax([], new Token(TokenKind.EndOfFile, new TextSpan(0, 0), string.Empty)),
                boundProgram: null,
                new CompilationStageOutput(),
                diagnostics,
                tokenCount: 0,
                ex);
        }
    }

    /// <summary>
    /// Compiles one Blade source string into a structured compilation output.
    /// </summary>
    public static CompilationOutput Compile(string text, string filePath, CompilationOptions? options = null)
    {
        SourceText source = new(text, filePath);
        DiagnosticBag diagnostics = new();
        if (!SourceFileLoader.Validate(source, diagnostics))
            return CreateFailedCompilationOutput(source, diagnostics);

        return CompileCore(source, diagnostics, options ?? new CompilationOptions());
    }

    private static CompilationOutput CompileCore(SourceText source, DiagnosticBag diagnostics, CompilationOptions effectiveOptions)
    {
        SourceText runtimeLauncherSource = CreateRuntimeLauncherSource(effectiveOptions.RuntimeLauncherPath, diagnostics);
        if (diagnostics.HasErrors)
            return CreateFailedCompilationOutput(source, diagnostics);

        LoadedCompilation loadedCompilation = CompilationModuleLoader.Load(source, runtimeLauncherSource, diagnostics, effectiveOptions.NamedModuleRoots);
        CompilationUnitSyntax unit = loadedCompilation.RootModule.Syntax;
        BoundProgram? boundProgram = null;
        CompilationStageOutput stages = new();

        try
        {
            if (!diagnostics.HasErrors)
                boundProgram = Binder.Bind(loadedCompilation, diagnostics, effectiveOptions.ComptimeFuel);

            if (boundProgram is not null && !diagnostics.HasErrors && effectiveOptions.EmitIr)
            {
                using IDisposable _ = diagnostics.UseSource(source);
                IrPipelineOptions pipelineOptions = new()
                {
                    EnableSingleCallsiteInlining = effectiveOptions.EnableSingleCallsiteInlining,
                    EnabledMirOptimizations = SortOptimizations(effectiveOptions.EnabledMirOptimizations),
                    EnabledLirOptimizations = SortOptimizations(effectiveOptions.EnabledLirOptimizations),
                    EnabledAsmirOptimizations = SortOptimizations(effectiveOptions.EnabledAsmirOptimizations),
                };
                IrPipeline.Build(boundProgram, stages, pipelineOptions, diagnostics);
            }

            CompilationStatus status = !diagnostics.HasErrors && (!effectiveOptions.EmitIr || stages.IsComplete)
                ? CompilationStatus.Succeeded
                : CompilationStatus.Failed;
            return CreateCompilationOutput(source, unit, boundProgram, stages, diagnostics.ToList(), loadedCompilation.RootModule.TokenCount, status, crash: null);
        }
        catch (Exception ex) when (IsRecoverableCompilerException(ex))
        {
            return CreateCrashedCompilationOutput(
                source,
                unit,
                boundProgram,
                stages,
                diagnostics,
                loadedCompilation.RootModule.TokenCount,
                ex);
        }
    }

    private static IReadOnlyList<T> SortOptimizations<T>(IReadOnlyList<T> optimizations) where T : Optimization
    {
        List<T> sorted = new(optimizations);
        sorted.Sort(static (a, b) =>
        {
            int cmp = b.Priority.CompareTo(a.Priority);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Name, b.Name);
        });
        return sorted;
    }

    private static CompilationOutput CreateFailedCompilationOutput(SourceText source, DiagnosticBag diagnostics)
    {
        Token eof = new(TokenKind.EndOfFile, new TextSpan(0, 0), string.Empty);
        CompilationUnitSyntax syntax = new([], eof);
        return CreateCompilationOutput(
            source,
            syntax,
            boundProgram: null,
            new CompilationStageOutput(),
            diagnostics.ToList(),
            tokenCount: 0,
            CompilationStatus.Failed,
            crash: null);
    }

    private static CompilationOutput CreateCrashedCompilationOutput(
        SourceText source,
        CompilationUnitSyntax syntax,
        BoundProgram? boundProgram,
        CompilationStageOutput stages,
        DiagnosticBag diagnostics,
        int tokenCount,
        Exception ex)
    {
        CompilationCrashInfo crash = new(ex.GetType().FullName ?? ex.GetType().Name, ex.Message, ex.ToString());
        return CreateCompilationOutput(source, syntax, boundProgram, stages, diagnostics.ToList(), tokenCount, CompilationStatus.Crashed, crash);
    }

    private static CompilationOutput CreateCompilationOutput(
        SourceText source,
        CompilationUnitSyntax syntax,
        BoundProgram? boundProgram,
        CompilationStageOutput stages,
        IReadOnlyList<Diagnostic> diagnostics,
        int tokenCount,
        CompilationStatus status,
        CompilationCrashInfo? crash)
    {
        CompilationOutput output = new(source, syntax, boundProgram, stages, diagnostics, tokenCount, status, crash);
        output.Metrics = new CompilationMetrics
        {
            TokenCount = output.TokenCount,
            MemberCount = output.Syntax.Members.Count,
            BoundFunctionCount = output.BoundProgram?.Functions.Count ?? 0,
            MirFunctionCount = output.Stages.MirModules?.Sum(static module => module.Functions.Count) ?? 0,
            TimeMs = 0,
        };
        return output;
    }

    private static bool IsRecoverableCompilerException(Exception ex)
    {
        return ex is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;
    }

    private static SourceText CreateRuntimeLauncherSource(string? runtimeLauncherPath, DiagnosticBag diagnostics)
    {
        Requires.NotNull(diagnostics);

        if (runtimeLauncherPath is null)
        {
            return new SourceText(DefaultRuntimeLauncherText, "<default-runtime>");
        }

        bool loaded = SourceFileLoader.TryLoad(runtimeLauncherPath, diagnostics, out SourceText runtimeSource);
        _ = loaded;
        return runtimeSource;
    }

    private const string DefaultRuntimeLauncherText = """
        import builtin;

        cog task _start {
            builtin.init_memory();
            builtin.task_main();
        }
        """;
}
