using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Blade.Roslyn.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnusedClassAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "BLD0001";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Unused class",
        messageFormat: "Class '{0}' is not reachable from the compiler entry point",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationAction(compilationContext =>
        {
            IMethodSymbol? entryPoint = compilationContext.Compilation.GetEntryPoint(compilationContext.CancellationToken);
            if (entryPoint is null)
                return;

            Analyze(compilationContext, entryPoint);
        });
    }

    // ── Entry point ──────────────────────────────────────────────────────────

    private static void Analyze(CompilationAnalysisContext ctx, IMethodSymbol entryPoint)
    {
        Compilation compilation = ctx.Compilation;
        CancellationToken cancellationToken = ctx.CancellationToken;

        HashSet<INamedTypeSymbol> reachedTypes = new(SymbolEqualityComparer.Default);
        HashSet<IMethodSymbol> seenMethods = new(SymbolEqualityComparer.Default);
        Queue<IMethodSymbol> worklist = new();

        // Plugin attribute classes — types decorated with these are discovered at runtime
        // via reflection and are therefore additional roots for the reachability walk.
        INamedTypeSymbol? mirAttr = compilation.GetTypeByMetadataName("Blade.IR.MirOptimizationAttribute");
        INamedTypeSymbol? lirAttr = compilation.GetTypeByMetadataName("Blade.IR.LirOptimizationAttribute");
        INamedTypeSymbol? asmAttr = compilation.GetTypeByMetadataName("Blade.IR.AsmOptimizationAttribute");

        foreach (INamedTypeSymbol type in CollectAssemblyTypes(compilation.Assembly))
        {
            if (HasPluginAttribute(type, mirAttr, lirAttr, asmAttr))
                ReachType(type, compilation, reachedTypes, seenMethods, worklist, cancellationToken);
        }

        // Seed from the compiler entry point and its containing class.
        ReachType(entryPoint.ContainingType, compilation, reachedTypes, seenMethods, worklist, cancellationToken);
        EnqueueMethod(entryPoint, seenMethods, worklist);

        // Process the worklist until stable.
        while (worklist.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IMethodSymbol method = worklist.Dequeue();
            ProcessMethod(method, compilation, reachedTypes, seenMethods, worklist, cancellationToken);
        }

        // Report types in this assembly that were never reached.
        foreach (INamedTypeSymbol candidate in CollectAssemblyTypes(compilation.Assembly))
        {
            if (!reachedTypes.Contains(candidate) && !IsCompilerGenerated(candidate))
            {
                ctx.ReportDiagnostic(
                    Diagnostic.Create(Rule, candidate.Locations[0], candidate.Name));
            }
        }
    }

    // ── Type collection ───────────────────────────────────────────────────────

    private static IEnumerable<INamedTypeSymbol> CollectAssemblyTypes(IAssemblySymbol assembly)
    {
        Queue<INamespaceOrTypeSymbol> queue = new();
        queue.Enqueue(assembly.GlobalNamespace);

        while (queue.Count > 0)
        {
            INamespaceOrTypeSymbol current = queue.Dequeue();

            if (current is INamedTypeSymbol named)
            {
                yield return named;
                foreach (INamedTypeSymbol nested in named.GetTypeMembers())
                    queue.Enqueue(nested);
            }
            else if (current is INamespaceSymbol ns)
            {
                foreach (INamespaceOrTypeSymbol member in ns.GetMembers())
                    queue.Enqueue(member);
            }
        }
    }

    // ── Reachability marking ──────────────────────────────────────────────────

    private static void ReachType(
        INamedTypeSymbol type,
        Compilation compilation,
        HashSet<INamedTypeSymbol> reachedTypes,
        HashSet<IMethodSymbol> seenMethods,
        Queue<IMethodSymbol> worklist,
        CancellationToken cancellationToken)
    {
        IAssemblySymbol targetAssembly = compilation.Assembly;

        if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, targetAssembly))
            return;
        if (!reachedTypes.Add(type))
            return; // already visited

        // For closed generics (e.g. Registry<MirOptimization>), also reach the open definition (Registry<T>).
        if (!SymbolEqualityComparer.Default.Equals(type, type.OriginalDefinition))
            ReachType((INamedTypeSymbol)type.OriginalDefinition, compilation, reachedTypes, seenMethods, worklist, cancellationToken);

        // Walk up the ContainingType chain — a nested type reaching implies its enclosing type is reached.
        if (type.ContainingType is INamedTypeSymbol enclosing)
            ReachType(enclosing, compilation, reachedTypes, seenMethods, worklist, cancellationToken);

        // Reach base type.
        if (type.BaseType is INamedTypeSymbol baseType)
            ReachType(baseType, compilation, reachedTypes, seenMethods, worklist, cancellationToken);

        // Reach implemented interfaces.
        foreach (INamedTypeSymbol iface in type.Interfaces)
            ReachType(iface, compilation, reachedTypes, seenMethods, worklist, cancellationToken);

        // Reach generic type arguments.
        foreach (ITypeSymbol arg in type.TypeArguments)
        {
            if (arg is INamedTypeSymbol namedArg)
                ReachType(namedArg, compilation, reachedTypes, seenMethods, worklist, cancellationToken);
        }

        // Reach attribute classes applied to the type declaration itself.
        foreach (AttributeData attr in type.GetAttributes())
        {
            if (attr.AttributeClass is INamedTypeSymbol attrClass)
                ReachType(attrClass, compilation, reachedTypes, seenMethods, worklist, cancellationToken);
        }

        // Walk members: enqueue methods and scan field/property declarations for initializer references.
        foreach (ISymbol member in type.GetMembers())
        {
            // Reach attribute classes applied to the member.
            foreach (AttributeData attr in member.GetAttributes())
            {
                if (attr.AttributeClass is INamedTypeSymbol attrClass)
                    ReachType(attrClass, compilation, reachedTypes, seenMethods, worklist, cancellationToken);
            }

            switch (member)
            {
                case IMethodSymbol method:
                    EnqueueMethod(method, seenMethods, worklist);
                    break;

                case IPropertySymbol property:
                    ReachTypeSymbol(property.Type, compilation, reachedTypes, seenMethods, worklist, cancellationToken);
                    if (property.GetMethod is not null)
                        EnqueueMethod(property.GetMethod, seenMethods, worklist);
                    if (property.SetMethod is not null)
                        EnqueueMethod(property.SetMethod, seenMethods, worklist);
                    // Scan property declarations for initializer expressions.
                    ScanDeclarations(property.DeclaringSyntaxReferences, compilation, reachedTypes, seenMethods, worklist, cancellationToken);
                    break;

                case IFieldSymbol field:
                    ReachTypeSymbol(field.Type, compilation, reachedTypes, seenMethods, worklist, cancellationToken);
                    // Scan field declarations for initializer expressions (e.g. `= new SomeType()`).
                    ScanDeclarations(field.DeclaringSyntaxReferences, compilation, reachedTypes, seenMethods, worklist, cancellationToken);
                    break;
            }
        }
    }

    private static void ReachTypeSymbol(
        ITypeSymbol typeSymbol,
        Compilation compilation,
        HashSet<INamedTypeSymbol> reachedTypes,
        HashSet<IMethodSymbol> seenMethods,
        Queue<IMethodSymbol> worklist,
        CancellationToken cancellationToken)
    {
        if (typeSymbol is INamedTypeSymbol named)
            ReachType(named, compilation, reachedTypes, seenMethods, worklist, cancellationToken);
    }

    private static void EnqueueMethod(
        IMethodSymbol method,
        HashSet<IMethodSymbol> seenMethods,
        Queue<IMethodSymbol> worklist)
    {
        if (seenMethods.Add(method))
            worklist.Enqueue(method);
    }

    // ── Method/declaration body walking ──────────────────────────────────────

    private static void ProcessMethod(
        IMethodSymbol method,
        Compilation compilation,
        HashSet<INamedTypeSymbol> reachedTypes,
        HashSet<IMethodSymbol> seenMethods,
        Queue<IMethodSymbol> worklist,
        CancellationToken cancellationToken)
    {
        // Reach types in the method signature.
        ReachTypeSymbol(method.ReturnType, compilation, reachedTypes, seenMethods, worklist, cancellationToken);
        foreach (IParameterSymbol param in method.Parameters)
            ReachTypeSymbol(param.Type, compilation, reachedTypes, seenMethods, worklist, cancellationToken);

        // Scan method body syntax.
        ScanDeclarations(method.DeclaringSyntaxReferences, compilation, reachedTypes, seenMethods, worklist, cancellationToken);
    }

    private static void ScanDeclarations(
        IEnumerable<SyntaxReference> syntaxReferences,
        Compilation compilation,
        HashSet<INamedTypeSymbol> reachedTypes,
        HashSet<IMethodSymbol> seenMethods,
        Queue<IMethodSymbol> worklist,
        CancellationToken cancellationToken)
    {
        foreach (SyntaxReference syntaxRef in syntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SyntaxNode root = syntaxRef.GetSyntax(cancellationToken);
#pragma warning disable RS1030 // Whole-compilation reachability analysis requires GetSemanticModel per tree
            SemanticModel model = compilation.GetSemanticModel(root.SyntaxTree);
#pragma warning restore RS1030

            foreach (SyntaxNode node in root.DescendantNodesAndSelf())
            {
                cancellationToken.ThrowIfCancellationRequested();

                ISymbol? symbol = model.GetSymbolInfo(node, cancellationToken).Symbol;
                if (symbol is null)
                    continue;

                IAssemblySymbol targetAssembly = compilation.Assembly;

                switch (symbol)
                {
                    case INamedTypeSymbol namedType:
                        ReachType(namedType, compilation, reachedTypes, seenMethods, worklist, cancellationToken);
                        break;

                    case IMethodSymbol calledMethod:
                        if (SymbolEqualityComparer.Default.Equals(calledMethod.ContainingAssembly, targetAssembly))
                        {
                            ReachType(calledMethod.ContainingType, compilation, reachedTypes, seenMethods, worklist, cancellationToken);
                            EnqueueMethod(calledMethod, seenMethods, worklist);

                            if (calledMethod.OverriddenMethod is IMethodSymbol overridden)
                                EnqueueMethod(overridden, seenMethods, worklist);
                        }
                        break;

                    case IFieldSymbol field:
                        ReachType(field.ContainingType, compilation, reachedTypes, seenMethods, worklist, cancellationToken);
                        ReachTypeSymbol(field.Type, compilation, reachedTypes, seenMethods, worklist, cancellationToken);
                        break;

                    case IPropertySymbol property:
                        ReachType(property.ContainingType, compilation, reachedTypes, seenMethods, worklist, cancellationToken);
                        ReachTypeSymbol(property.Type, compilation, reachedTypes, seenMethods, worklist, cancellationToken);
                        if (property.GetMethod is not null)
                            EnqueueMethod(property.GetMethod, seenMethods, worklist);
                        if (property.SetMethod is not null)
                            EnqueueMethod(property.SetMethod, seenMethods, worklist);
                        break;

                    case ILocalSymbol local:
                        ReachTypeSymbol(local.Type, compilation, reachedTypes, seenMethods, worklist, cancellationToken);
                        break;
                }
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool HasPluginAttribute(
        INamedTypeSymbol type,
        INamedTypeSymbol? mirAttr,
        INamedTypeSymbol? lirAttr,
        INamedTypeSymbol? asmAttr)
    {
        foreach (AttributeData attr in type.GetAttributes())
        {
            INamedTypeSymbol? attrClass = attr.AttributeClass;
            if (attrClass is null)
                continue;
            if (SymbolEqualityComparer.Default.Equals(attrClass, mirAttr) ||
                SymbolEqualityComparer.Default.Equals(attrClass, lirAttr) ||
                SymbolEqualityComparer.Default.Equals(attrClass, asmAttr))
                return true;
        }
        return false;
    }

    private static bool IsCompilerGenerated(INamedTypeSymbol type)
    {
        // Compiler-generated types (closures, state machines, display classes)
        // have unspeakable names starting with '<'.
        return type.Name.Length > 0 && type.Name[0] == '<';
    }
}
