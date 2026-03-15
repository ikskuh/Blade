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
    public IReadOnlyList<OptimizationDirective> OptimizationDirectives { get; init; } = [];
}

public sealed class CompilationResult
{
    public CompilationResult(
        SourceText source,
        CompilationUnitSyntax syntax,
        BoundProgram boundProgram,
        IrBuildResult? irBuildResult,
        IReadOnlyList<Diagnostic> diagnostics,
        int tokenCount)
    {
        Source = source;
        Syntax = syntax;
        BoundProgram = boundProgram;
        IrBuildResult = irBuildResult;
        Diagnostics = diagnostics;
        TokenCount = tokenCount;
    }

    public SourceText Source { get; }
    public CompilationUnitSyntax Syntax { get; }
    public BoundProgram BoundProgram { get; }
    public IrBuildResult? IrBuildResult { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public int TokenCount { get; }
}

public static class CompilerDriver
{
    public static CompilationResult Compile(string text, string filePath, CompilationOptions? options = null)
    {
        CompilationOptions effectiveOptions = options ?? new CompilationOptions();
        SourceText source = new(text, filePath);
        DiagnosticBag diagnostics = new();

        Parser parser = Parser.Create(source, diagnostics);
        CompilationUnitSyntax unit = parser.ParseCompilationUnit();
        BoundProgram boundProgram = Binder.Bind(unit, diagnostics);

        IrBuildResult? irBuildResult = null;
        if (diagnostics.Count == 0)
        {
            IrPipelineOptions pipelineOptions = new()
            {
                EnableSingleCallsiteInlining = effectiveOptions.EnableSingleCallsiteInlining,
                OptimizationDirectives = effectiveOptions.OptimizationDirectives,
            };
            irBuildResult = IrPipeline.Build(boundProgram, pipelineOptions);
        }

        List<Diagnostic> diagnosticList = diagnostics.ToList();
        return new CompilationResult(source, unit, boundProgram, irBuildResult, diagnosticList, parser.TokenCount);
    }
}
