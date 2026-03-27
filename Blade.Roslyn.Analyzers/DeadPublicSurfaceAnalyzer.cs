using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Blade.Roslyn.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DeadPublicSurfaceAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "DC0001";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Unused public symbol",
        messageFormat: "Public symbol '{0}' is not reachable from the configured roots",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(startContext =>
        {
            var entryPoint = startContext.Compilation.GetEntryPoint(startContext.CancellationToken);

            // collect roots, candidates, and edges here

            startContext.RegisterCompilationEndAction(endContext =>
            {
                // solve reachability and report diagnostics here
            });
        });
    }
}