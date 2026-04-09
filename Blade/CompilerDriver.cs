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
    public RuntimeTemplate? RuntimeTemplate { get; init; }
}

public sealed class CompilationResult(
    SourceText source,
    CompilationUnitSyntax syntax,
    BoundModule boundModule,
    IrBuildResult? irBuildResult,
    IReadOnlyList<Diagnostic> diagnostics,
    int tokenCount)
{
    public SourceText Source { get; } = source;
    public CompilationUnitSyntax Syntax { get; } = syntax;
    public BoundModule BoundModule { get; } = boundModule;
    public IrBuildResult? IrBuildResult { get; } = irBuildResult;
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;
    public int TokenCount { get; } = tokenCount;
}

public static class CompilerDriver
{
    public static CompilationResult CompileFile(string filePath, CompilationOptions? options = null)
    {
        DiagnosticBag diagnostics = new();
        bool sourceIsValid = SourceFileLoader.TryLoad(filePath, diagnostics, out SourceText source);
        if (!sourceIsValid)
            return CreateFailedCompilationResult(source, diagnostics);

        return CompileCore(source, diagnostics, options ?? new CompilationOptions());
    }

    public static CompilationResult Compile(string text, string filePath, CompilationOptions? options = null)
    {
        SourceText source = new(text, filePath);
        DiagnosticBag diagnostics = new();
        if (!SourceFileLoader.Validate(source, diagnostics))
            return CreateFailedCompilationResult(source, diagnostics);

        return CompileCore(source, diagnostics, options ?? new CompilationOptions());
    }

    private static CompilationResult CompileCore(SourceText source, DiagnosticBag diagnostics, CompilationOptions effectiveOptions)
    {
        LoadedCompilation loadedCompilation = CompilationModuleLoader.Load(source, diagnostics, effectiveOptions.NamedModuleRoots);
        CompilationUnitSyntax unit = loadedCompilation.RootModule.Syntax;

        BoundModule boundModule = CreateEmptyBoundModule();
        if (!diagnostics.HasErrors)
            boundModule = Binder.Bind(loadedCompilation, diagnostics, effectiveOptions.ComptimeFuel);

        IrBuildResult? irBuildResult = null;
        if (!diagnostics.HasErrors && effectiveOptions.EmitIr)
        {
            using IDisposable _ = diagnostics.UseSource(source);
            IrPipelineOptions pipelineOptions = new()
            {
                EnableSingleCallsiteInlining = effectiveOptions.EnableSingleCallsiteInlining,
                EnabledMirOptimizations = SortOptimizations(effectiveOptions.EnabledMirOptimizations),
                EnabledLirOptimizations = SortOptimizations(effectiveOptions.EnabledLirOptimizations),
                EnabledAsmirOptimizations = SortOptimizations(effectiveOptions.EnabledAsmirOptimizations),
                RuntimeTemplate = effectiveOptions.RuntimeTemplate,
            };
            irBuildResult = IrPipeline.Build(boundModule, pipelineOptions, diagnostics);
        }

        List<Diagnostic> diagnosticList = diagnostics.ToList();
        return new CompilationResult(source, unit, boundModule, irBuildResult, diagnosticList, loadedCompilation.RootModule.TokenCount);
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

    private static CompilationResult CreateFailedCompilationResult(SourceText source, DiagnosticBag diagnostics)
    {
        Token eof = new(TokenKind.EndOfFile, new TextSpan(0, 0), string.Empty);
        CompilationUnitSyntax syntax = new([], eof);
        BoundModule boundModule = CreateEmptyBoundModule();
        return new CompilationResult(source, syntax, boundModule, null, diagnostics.ToList(), 0);
    }

    private static BoundModule CreateEmptyBoundModule()
    {
        Token eof = new(TokenKind.EndOfFile, new TextSpan(0, 0), string.Empty);
        CompilationUnitSyntax syntax = new([], eof);
        return new BoundModule(
            "<empty>",
            syntax,
            [],
            [],
            [],
            new Dictionary<string, TypeSymbol>(),
            new Dictionary<string, FunctionSymbol>(),
            new Dictionary<string, VariableSymbol>(),
            new Dictionary<string, BoundModule>());
    }
}
