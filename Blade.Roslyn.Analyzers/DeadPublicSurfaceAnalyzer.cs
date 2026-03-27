using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Blade.Roslyn.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnusedClassAnalyzer : DiagnosticAnalyzer
{
    public const string ClassDiagnosticId = "BLD0001";
    public const string MethodDiagnosticId = "BLD0002";

    private static readonly DiagnosticDescriptor ClassRule = new(
        id: ClassDiagnosticId,
        title: "Unused class",
        messageFormat: "Class '{0}' is not reachable from the compiler entry point",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly DiagnosticDescriptor MethodRule = new(
        id: MethodDiagnosticId,
        title: "Unused method or constructor",
        messageFormat: "'{0}.{1}' is not reachable from the compiler entry point",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [ClassRule, MethodRule];

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

        // reachedTypes: O(1) membership checks.
        // reachedTypesList: ordered mirror for indexed iteration without allocating snapshots.
        // Invariant: reachedTypes and reachedTypesList always contain the same elements.
        HashSet<INamedTypeSymbol> reachedTypes = new(SymbolEqualityComparer.Default);
        List<INamedTypeSymbol> reachedTypesList = [];
        HashSet<IMethodSymbol> seenMethods = new(SymbolEqualityComparer.Default);
        Queue<IMethodSymbol> worklist = new();

        // Plugin attribute and interface symbols — types with these attributes are discovered
        // via reflection at runtime and are therefore additional roots.
        INamedTypeSymbol? mirAttr = compilation.GetTypeByMetadataName("Blade.IR.MirOptimizationAttribute");
        INamedTypeSymbol? lirAttr = compilation.GetTypeByMetadataName("Blade.IR.LirOptimizationAttribute");
        INamedTypeSymbol? asmAttr = compilation.GetTypeByMetadataName("Blade.IR.AsmOptimizationAttribute");
        INamedTypeSymbol? mirIface = compilation.GetTypeByMetadataName("Blade.IR.IMirOptimization");
        INamedTypeSymbol? lirIface = compilation.GetTypeByMetadataName("Blade.IR.ILirOptimization");
        INamedTypeSymbol? asmIface = compilation.GetTypeByMetadataName("Blade.IR.IAsmOptimization");
        INamedTypeSymbol? publicApiAttr = compilation.GetTypeByMetadataName("Blade.PublicApiAttribute");

        // PublicApiAttribute is a meta-attribute used by developers to suppress BLD0002;
        // it is never referenced in Blade source code directly, so seed it explicitly.
        if (publicApiAttr is not null)
            AddReachedType(publicApiAttr, reachedTypes, reachedTypesList);

        // Seed plugin types: only the interface contract method (Run) is enqueued,
        // so unused helpers on plugin types will be caught by BLD0002.
        foreach (INamedTypeSymbol type in CollectAssemblyTypes(compilation.Assembly))
        {
            SeedPluginTypeIfApplicable(type, mirAttr, lirAttr, asmAttr, mirIface, lirIface, asmIface,
                compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
        }

        // Seed from the compiler entry point and its containing class.
        ReachType(entryPoint.ContainingType, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
        EnqueueMethod(entryPoint, seenMethods, worklist);

        // Phase 1: drain the worklist (direct calls, field initializers, signatures).
        // Terminates: each method is added to seenMethods at most once (Add returns false on
        // re-insertion), so the worklist is bounded by the finite number of assembly methods.
        DrainWorklist(worklist, compilation, reachedTypes, reachedTypesList, seenMethods, cancellationToken);

        // Phase 2: virtual dispatch.
        // When a virtual/abstract method M is seen, concrete overrides of M on reached types
        // are not discovered by static call-site analysis alone. We walk reached types in
        // arrival order: each type is checked exactly once per "generation" of newly-seen methods.
        //
        // Terminates: virtualCheckCursor only advances (never moves backward), bounded by
        // reachedTypesList.Count. DrainWorklist in each iteration terminates by the same
        // argument as Phase 1. The outer while loop exits when no new types are found.
        int virtualCheckCursor = 0;
        while (virtualCheckCursor < reachedTypesList.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int end = reachedTypesList.Count;
            for (int i = virtualCheckCursor; i < end; i++)
            {
                foreach (ISymbol member in reachedTypesList[i].GetMembers())
                {
                    if (member is IMethodSymbol method
                        && method.OverriddenMethod is IMethodSymbol overridden
                        && seenMethods.Contains(overridden))
                    {
                        EnqueueMethod(method, seenMethods, worklist);
                    }
                }
            }
            virtualCheckCursor = end;

            // Drain: new methods may reach new types, extending reachedTypesList beyond `end`.
            DrainWorklist(worklist, compilation, reachedTypes, reachedTypesList, seenMethods, cancellationToken);
        }

        // Report BLD0001: types in this assembly that were never reached.
        foreach (INamedTypeSymbol candidate in CollectAssemblyTypes(compilation.Assembly))
        {
            if (!reachedTypes.Contains(candidate) && !IsCompilerGenerated(candidate))
            {
                ctx.ReportDiagnostic(
                    Diagnostic.Create(ClassRule, candidate.Locations[0], candidate.Name));
            }
        }

        // Report BLD0002: methods/constructors on reachable types that were never called.
        foreach (INamedTypeSymbol reachedType in reachedTypesList)
        {
            foreach (ISymbol member in reachedType.GetMembers())
            {
                if (member is not IMethodSymbol method)
                    continue;
                if (!IsReportableMethod(method))
                    continue;
                if (seenMethods.Contains(method))
                    continue;
                if (HasAttribute(method, publicApiAttr))
                    continue;

                ctx.ReportDiagnostic(
                    Diagnostic.Create(MethodRule, method.Locations[0], reachedType.Name, method.Name));
            }
        }
    }

    // ── Plugin seeding ────────────────────────────────────────────────────────

    private static void SeedPluginTypeIfApplicable(
        INamedTypeSymbol type,
        INamedTypeSymbol? mirAttr,
        INamedTypeSymbol? lirAttr,
        INamedTypeSymbol? asmAttr,
        INamedTypeSymbol? mirIface,
        INamedTypeSymbol? lirIface,
        INamedTypeSymbol? asmIface,
        Compilation compilation,
        HashSet<INamedTypeSymbol> reachedTypes,
        List<INamedTypeSymbol> reachedTypesList,
        HashSet<IMethodSymbol> seenMethods,
        Queue<IMethodSymbol> worklist,
        CancellationToken cancellationToken)
    {
        foreach (AttributeData attr in type.GetAttributes())
        {
            INamedTypeSymbol? iface =
                SymbolEqualityComparer.Default.Equals(attr.AttributeClass, mirAttr) ? mirIface :
                SymbolEqualityComparer.Default.Equals(attr.AttributeClass, lirAttr) ? lirIface :
                SymbolEqualityComparer.Default.Equals(attr.AttributeClass, asmAttr) ? asmIface : null;

            if (iface is null)
                continue;

            // Mark the type as reached so it isn't reported as an unused class.
            AddReachedType(type, reachedTypes, reachedTypesList);

            // Reach base types and interfaces (so PerFunctionAsmOptimization etc. are not flagged).
            ReachTypeHierarchy(type, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);

            // Only enqueue the interface contract method (Run). Any helpers called by Run
            // will be discovered transitively; helpers not called by Run are dead code.
            IMethodSymbol? ifaceRun = iface.GetMembers("Run").OfType<IMethodSymbol>().FirstOrDefault();
            if (ifaceRun is null)
                continue;

            ISymbol? impl = type.FindImplementationForInterfaceMember(ifaceRun);
            if (impl is IMethodSymbol runImpl)
                EnqueueMethod(runImpl, seenMethods, worklist);
        }
    }

    // Reaches base type and interfaces of a type without enqueuing its own members.
    // Used when member seeding is controlled separately (e.g. plugin types).
    private static void ReachTypeHierarchy(
        INamedTypeSymbol type,
        Compilation compilation,
        HashSet<INamedTypeSymbol> reachedTypes,
        List<INamedTypeSymbol> reachedTypesList,
        HashSet<IMethodSymbol> seenMethods,
        Queue<IMethodSymbol> worklist,
        CancellationToken cancellationToken)
    {
        if (type.BaseType is INamedTypeSymbol baseType)
            ReachType(baseType, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
        foreach (INamedTypeSymbol iface in type.Interfaces)
            ReachType(iface, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
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

    // Adds type to both the set (O(1) lookup) and the list (ordered iteration).
    // Returns true if the type was not previously reached.
    private static bool AddReachedType(
        INamedTypeSymbol type,
        HashSet<INamedTypeSymbol> reachedTypes,
        List<INamedTypeSymbol> reachedTypesList)
    {
        if (!reachedTypes.Add(type))
            return false;
        reachedTypesList.Add(type);
        return true;
    }

    private static void ReachType(
        INamedTypeSymbol type,
        Compilation compilation,
        HashSet<INamedTypeSymbol> reachedTypes,
        List<INamedTypeSymbol> reachedTypesList,
        HashSet<IMethodSymbol> seenMethods,
        Queue<IMethodSymbol> worklist,
        CancellationToken cancellationToken)
    {
        IAssemblySymbol targetAssembly = compilation.Assembly;

        if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, targetAssembly))
            return;
        if (!AddReachedType(type, reachedTypes, reachedTypesList))
            return; // already visited

        // For closed generics (e.g. Registry<MirOptimization>), also reach the open definition (Registry<T>).
        if (!SymbolEqualityComparer.Default.Equals(type, type.OriginalDefinition))
            ReachType((INamedTypeSymbol)type.OriginalDefinition, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);

        // Walk up the ContainingType chain — a nested type reaching implies its enclosing type is reached.
        if (type.ContainingType is INamedTypeSymbol enclosing)
            ReachType(enclosing, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);

        // Reach base type.
        if (type.BaseType is INamedTypeSymbol baseType)
            ReachType(baseType, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);

        // Reach implemented interfaces.
        foreach (INamedTypeSymbol iface in type.Interfaces)
            ReachType(iface, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);

        // Reach generic type arguments.
        foreach (ITypeSymbol arg in type.TypeArguments)
        {
            if (arg is INamedTypeSymbol namedArg)
                ReachType(namedArg, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
        }

        // Reach attribute classes applied to the type declaration itself.
        foreach (AttributeData attr in type.GetAttributes())
        {
            if (attr.AttributeClass is INamedTypeSymbol attrClass)
                ReachType(attrClass, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
        }

        // Walk members: enqueue methods and scan field/property declarations for initializer references.
        foreach (ISymbol member in type.GetMembers())
        {
            // Reach attribute classes applied to the member.
            foreach (AttributeData attr in member.GetAttributes())
            {
                if (attr.AttributeClass is INamedTypeSymbol attrClass)
                    ReachType(attrClass, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
            }

            switch (member)
            {
                case IMethodSymbol method:
                    EnqueueMethod(method, seenMethods, worklist);
                    break;

                case IPropertySymbol property:
                    ReachTypeSymbol(property.Type, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
                    if (property.GetMethod is not null)
                        EnqueueMethod(property.GetMethod, seenMethods, worklist);
                    if (property.SetMethod is not null)
                        EnqueueMethod(property.SetMethod, seenMethods, worklist);
                    // Scan property declarations for initializer expressions.
                    ScanDeclarations(property.DeclaringSyntaxReferences, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
                    break;

                case IFieldSymbol field:
                    ReachTypeSymbol(field.Type, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
                    // Scan field declarations for initializer expressions (e.g. `= new SomeType()`).
                    ScanDeclarations(field.DeclaringSyntaxReferences, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
                    break;
            }
        }
    }

    private static void ReachTypeSymbol(
        ITypeSymbol typeSymbol,
        Compilation compilation,
        HashSet<INamedTypeSymbol> reachedTypes,
        List<INamedTypeSymbol> reachedTypesList,
        HashSet<IMethodSymbol> seenMethods,
        Queue<IMethodSymbol> worklist,
        CancellationToken cancellationToken)
    {
        if (typeSymbol is INamedTypeSymbol named)
            ReachType(named, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
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

    private static void DrainWorklist(
        Queue<IMethodSymbol> worklist,
        Compilation compilation,
        HashSet<INamedTypeSymbol> reachedTypes,
        List<INamedTypeSymbol> reachedTypesList,
        HashSet<IMethodSymbol> seenMethods,
        CancellationToken cancellationToken)
    {
        while (worklist.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessMethod(worklist.Dequeue(), compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
        }
    }

    private static void ProcessMethod(
        IMethodSymbol method,
        Compilation compilation,
        HashSet<INamedTypeSymbol> reachedTypes,
        List<INamedTypeSymbol> reachedTypesList,
        HashSet<IMethodSymbol> seenMethods,
        Queue<IMethodSymbol> worklist,
        CancellationToken cancellationToken)
    {
        // Reach types in the method signature.
        ReachTypeSymbol(method.ReturnType, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
        foreach (IParameterSymbol param in method.Parameters)
            ReachTypeSymbol(param.Type, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);

        // Scan method body syntax.
        ScanDeclarations(method.DeclaringSyntaxReferences, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
    }

    private static void ScanDeclarations(
        IEnumerable<SyntaxReference> syntaxReferences,
        Compilation compilation,
        HashSet<INamedTypeSymbol> reachedTypes,
        List<INamedTypeSymbol> reachedTypesList,
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

            IAssemblySymbol targetAssembly = compilation.Assembly;

            foreach (SyntaxNode node in root.DescendantNodesAndSelf())
            {
                cancellationToken.ThrowIfCancellationRequested();

                ISymbol? symbol = model.GetSymbolInfo(node, cancellationToken).Symbol;
                if (symbol is null)
                    continue;

                switch (symbol)
                {
                    case INamedTypeSymbol namedType:
                        ReachType(namedType, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
                        break;

                    case IMethodSymbol calledMethod:
                        if (SymbolEqualityComparer.Default.Equals(calledMethod.ContainingAssembly, targetAssembly))
                        {
                            ReachType(calledMethod.ContainingType, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
                            EnqueueMethod(calledMethod, seenMethods, worklist);

                            if (calledMethod.OverriddenMethod is IMethodSymbol overridden)
                                EnqueueMethod(overridden, seenMethods, worklist);
                        }
                        break;

                    case IFieldSymbol field:
                        ReachType(field.ContainingType, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
                        ReachTypeSymbol(field.Type, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
                        break;

                    case IPropertySymbol property:
                        ReachType(property.ContainingType, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
                        ReachTypeSymbol(property.Type, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
                        if (property.GetMethod is not null)
                            EnqueueMethod(property.GetMethod, seenMethods, worklist);
                        if (property.SetMethod is not null)
                            EnqueueMethod(property.SetMethod, seenMethods, worklist);
                        break;

                    case ILocalSymbol local:
                        ReachTypeSymbol(local.Type, compilation, reachedTypes, reachedTypesList, seenMethods, worklist, cancellationToken);
                        break;
                }
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsReportableMethod(IMethodSymbol method)
    {
        if (method.IsImplicitlyDeclared)
            return false;
        return method.MethodKind switch
        {
            MethodKind.Ordinary => true,
            MethodKind.Constructor => true,
            _ => false,
        };
    }

    private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol? attributeType)
    {
        if (attributeType is null)
            return false;
        foreach (AttributeData attr in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attributeType))
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
