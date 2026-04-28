using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Blade;
using Blade.Diagnostics;
using Blade.Semantics.Bound;
using Blade.Source;
using Blade.Syntax;
using Blade.Syntax.Nodes;

namespace Blade.Semantics;

public sealed class Binder
{
    private const string BuiltinModulePath = "<builtin>";
    private readonly DiagnosticBag _diagnostics;
    private readonly LoadedCompilation _compilation;
    private readonly Dictionary<string, TypeSymbol> _typeAliases = new(StringComparer.Ordinal);
    private readonly Dictionary<TypeSymbol, TypeAliasDeclarationSyntax> _typeAliasDeclarations = [];
    private readonly HashSet<string> _typeAliasResolutionStack = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LayoutSymbol> _layouts = new(StringComparer.Ordinal);
    private readonly Dictionary<LayoutSymbol, MemberSyntax> _layoutDeclarations = [];
    private readonly Dictionary<LayoutSymbol, IReadOnlyDictionary<string, IReadOnlyList<LayoutMemberBinding>>> _layoutVisibleMemberCache = [];
    private readonly Dictionary<string, FunctionSymbol> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BoundModule> _importedModules = new(StringComparer.Ordinal);
    private readonly Dictionary<FunctionSymbol, BoundBlockStatement> _boundFunctionBodies = new();
    private readonly Dictionary<FunctionSymbol, ComptimeSupportResult> _comptimeSupportCache = new();
    private readonly Dictionary<string, BoundModule> _moduleDefinitionCache;
    private readonly HashSet<string> _moduleBindingStack;
    private readonly Scope _globalScope;
    private Scope _currentScope;
    private FunctionSymbol? _currentFunction;
    private SourceText? _currentSource;
    private readonly Stack<LoopContext> _loopStack = new();
    private readonly int _comptimeFuel;
    private int _anonymousStructIndex;
    private bool _suppressPointerStorageClassDiagnostics;
    private LayoutSymbol? _currentImplicitLayout;
    private IReadOnlyList<LayoutSymbol> _currentEffectiveLayouts = [];

    private static readonly EnumTypeSymbol MemorySpaceType = new("MemorySpace", BuiltinTypes.U32,
        new Dictionary<string, long>(StringComparer.Ordinal) { ["cog"] = 0, ["lut"] = 1, ["hub"] = 2, ["_cog"] = 0, ["_lut"] = 1, ["_hub"] = 2 },
        isOpen: false);

    private readonly record struct LayoutMemberBinding(LayoutSymbol Layout, GlobalVariableSymbol Variable);
    private readonly record struct StoredLayoutMemberBinding(VariableDeclarationSyntax Declaration, GlobalVariableSymbol Symbol);
    private readonly record struct TaskLocalFunctionBinding(FunctionSymbol Symbol, SyntaxNode Syntax);

    private sealed class BindContext : IDisposable
    {
        private readonly Binder _binder;
        private readonly Scope _previousScope;
        private readonly FunctionSymbol? _previousFunction;
        private readonly LayoutSymbol? _previousImplicitLayout;
        private readonly IReadOnlyList<LayoutSymbol> _previousEffectiveLayouts;

        public BindContext(
            Binder binder,
            Scope scope,
            FunctionSymbol? function,
            LayoutSymbol? implicitLayout,
            IReadOnlyList<LayoutSymbol> effectiveLayouts)
        {
            _binder = Requires.NotNull(binder);
            _previousScope = binder._currentScope;
            _previousFunction = binder._currentFunction;
            _previousImplicitLayout = binder._currentImplicitLayout;
            _previousEffectiveLayouts = binder._currentEffectiveLayouts;

            binder._currentScope = Requires.NotNull(scope);
            binder._currentFunction = function;
            binder._currentImplicitLayout = implicitLayout;
            binder._currentEffectiveLayouts = Requires.NotNull(effectiveLayouts);
        }

        public void Dispose()
        {
            _binder._currentEffectiveLayouts = _previousEffectiveLayouts;
            _binder._currentImplicitLayout = _previousImplicitLayout;
            _binder._currentFunction = _previousFunction;
            _binder._currentScope = _previousScope;
        }
    }

    private Binder(
        DiagnosticBag diagnostics,
        LoadedCompilation compilation,
        HashSet<string> moduleBindingStack,
        Dictionary<string, BoundModule> moduleDefinitionCache,
        int comptimeFuel)
    {
        _diagnostics = diagnostics;
        _compilation = Requires.NotNull(compilation);
        _moduleBindingStack = Requires.NotNull(moduleBindingStack);
        _moduleDefinitionCache = Requires.NotNull(moduleDefinitionCache);
        _comptimeFuel = Requires.Positive(comptimeFuel);
        _globalScope = new Scope(parent: null);
        _currentScope = _globalScope;
    }

    internal static BoundProgram? Bind(
        LoadedCompilation compilation,
        DiagnosticBag diagnostics,
        int comptimeFuel = 250)
    {
        Requires.NotNull(compilation);
        Requires.NotNull(diagnostics);

        HashSet<string> moduleBindingStack = new(PathIdentity.Comparer)
        {
            compilation.RootModule.FullPath,
        };
        Dictionary<string, BoundModule> moduleDefinitionCache = new(PathIdentity.Comparer);
        Binder binder = new(diagnostics, compilation, moduleBindingStack, moduleDefinitionCache, comptimeFuel);
        BoundModule rootModule = binder.BindCompilationUnit(compilation.RootModule);
        using IDisposable _ = diagnostics.UseSource(compilation.RootModule.Source);
        return binder.CreateBoundProgram(rootModule);
    }

    private BoundModule BindCompilationUnit(LoadedModule module)
    {
        using IDisposable _ = _diagnostics.UseSource(module.Source);
        _currentSource = module.Source;

        CompilationUnitSyntax unit = module.Syntax;

        BindImports(module);
        CollectTopLevelTypes(unit);
        CollectTopLevelLayouts(unit);
        CollectTopLevelFunctions(unit);
        ResolveFunctionMetadata(_functions.Values, _globalScope);
        ResolveFunctionSignatures();
        ResolveTaskSignatures();
        ResolveLayoutParents();
        DeclareTopLevelVariables(unit);
        ResolveAllTypeAliases();

        List<GlobalVariableSymbol> boundGlobals = new();
        List<BoundFunctionMember> ordinaryFunctions = new();

        foreach (MemberSyntax member in unit.Members)
        {
            switch (member)
            {
                case VariableDeclarationSyntax variable when MapStorageClass(variable.StorageClassKeyword) is not null:
                    _currentScope = _globalScope;
                    if (IsSupportedPlainTopLevelGlobal(variable, reportDiagnostic: true))
                        boundGlobals.Add(BindGlobalVariable(variable));
                    break;

                case LayoutDeclarationSyntax layoutDeclaration:
                    if (_layouts.TryGetValue(layoutDeclaration.Name.Text, out LayoutSymbol? boundLayout))
                        BindLayoutDeclaration(Requires.NotNull(boundLayout), layoutDeclaration, boundGlobals);
                    break;

                case TaskDeclarationSyntax taskDeclaration:
                    if (_layouts.TryGetValue(taskDeclaration.Name.Text, out LayoutSymbol? taskLayout)
                        && taskLayout is TaskSymbol taskSymbol)
                    {
                        BindTaskDeclaration(taskSymbol, taskDeclaration, boundGlobals, ordinaryFunctions);
                    }
                    break;

                case FunctionDeclarationSyntax function:
                    if (_functions.ContainsKey(function.Name.Text))
                        ordinaryFunctions.Add(BindFunction(function));
                    break;

                case AsmFunctionDeclarationSyntax asmFunction:
                    if (_functions.ContainsKey(asmFunction.Name.Text))
                        ordinaryFunctions.Add(BindAsmFunction(asmFunction));
                    break;
            }
        }

        Dictionary<string, Symbol> exportedSymbols = CreateExportedSymbols();

        return new BoundModule(
            module.FullPath,
            unit,
            boundGlobals,
            ordinaryFunctions,
            exportedSymbols);
    }

    private BoundProgram? CreateBoundProgram(BoundModule rootModule)
    {
        Requires.NotNull(rootModule);
        if (!TryResolveProgramEntryPoint(rootModule, out TaskSymbol? entryPoint, out BoundFunctionMember? entryPointFunction))
            return null;

        TaskSymbol requiredEntryPoint = Requires.NotNull(entryPoint);
        BoundFunctionMember requiredEntryPointFunction = Requires.NotNull(entryPointFunction);

        List<BoundModule> modules = [rootModule];
        foreach (BoundModule module in _moduleDefinitionCache
                     .Where(static entry => !ReferenceEquals(entry.Value, null))
                     .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                     .Select(static entry => entry.Value))
        {
            if (!ReferenceEquals(module, rootModule))
                modules.Add(module);
        }

        List<GlobalVariableSymbol> globalVariables = new();
        List<BoundFunctionMember> functions = new();
        foreach (BoundModule module in modules)
        {
            globalVariables.AddRange(module.GlobalVariables);
            functions.AddRange(module.Functions);
        }

        if (!functions.Any(function => ReferenceEquals(function.Symbol, requiredEntryPoint.EntryFunction)))
            functions.Insert(0, requiredEntryPointFunction);

        return new BoundProgram(rootModule, requiredEntryPoint, requiredEntryPointFunction, modules, globalVariables, functions);
    }

    private bool TryResolveProgramEntryPoint(BoundModule rootModule, out TaskSymbol? entryPoint, out BoundFunctionMember? entryPointFunction)
    {
        Requires.NotNull(rootModule);
        if (!rootModule.ExportedSymbols.TryGetValue("main", out Symbol? symbol)
            || symbol is not TaskSymbol task)
        {
            _diagnostics.Report(new MissingMainTaskError(_diagnostics.CurrentSource, rootModule.Syntax.EndOfFileToken.Span));
            entryPoint = null;
            entryPointFunction = null;
            return false;
        }

        if (task.StorageClass != VariableStorageClass.Cog)
            _diagnostics.ReportMainTaskMustBeCog(task.SourceSpan.Span, task.Name, task.StorageClass);

        foreach (BoundFunctionMember function in rootModule.Functions)
        {
            if (ReferenceEquals(function.Symbol, task.EntryFunction))
            {
                entryPoint = task;
                entryPointFunction = function;
                return true;
            }
        }

        Assert.Unreachable($"Entry task '{task.Name}' must have a bound function body in the root module."); // pragma: force-coverage
        entryPoint = Assert.UnreachableValue<TaskSymbol>(); // pragma: force-coverage
        entryPointFunction = Assert.UnreachableValue<BoundFunctionMember>(); // pragma: force-coverage
        return false; // pragma: force-coverage
    }

    private void BindImports(LoadedModule module)
    {
        foreach (LoadedImport import in module.Imports)
        {
            BoundModule imported;

            if (import.Kind == LoadedImportKind.Builtin)
            {
                imported = GetOrCreateBuiltinModule();
            }
            else
            {
                Assert.Invariant(import.Kind == LoadedImportKind.File, "Non-builtin imports must be file-backed.");
                Assert.Invariant(import.ResolvedFullPath is not null, "Binder imports must be fully resolved and loaded before binding.");
                imported = LoadAndBindModule(import.ResolvedFullPath, import.Syntax.Source.Span);
            }
            TextSpan importSpan = import.Syntax.Alias?.Span ?? import.Syntax.Source.Span;
            if (!TryDeclareSymbol(_globalScope, new ModuleSymbol(import.Alias, imported, CreateSourceSpan(importSpan)), importSpan))
                continue;

            _importedModules.Add(import.Alias, imported);
        }
    }

    private BoundModule LoadAndBindModule(string resolvedFullPath, TextSpan importSiteSpan)
    {
        if (_moduleDefinitionCache.TryGetValue(resolvedFullPath, out BoundModule? cachedDefinition))
            return cachedDefinition;

        Assert.Invariant(
            _compilation.ModulesByFullPath.TryGetValue(resolvedFullPath, out LoadedModule? loadedModule),
            $"Imported module '{resolvedFullPath}' must be loaded before binding.");
        LoadedModule imported = Requires.NotNull(loadedModule);

        if (_moduleBindingStack.Contains(resolvedFullPath))
        {
            _diagnostics.Report(new CircularImportError(_diagnostics.CurrentSource, importSiteSpan, resolvedFullPath));
            return CreateEmptyImportedModule(resolvedFullPath, imported.Syntax);
        }

        _moduleBindingStack.Add(resolvedFullPath);
        BoundModule program;
        try
        {
            Binder nestedBinder = new(_diagnostics, _compilation, _moduleBindingStack, _moduleDefinitionCache, _comptimeFuel);
            program = nestedBinder.BindCompilationUnit(imported);
        }
        finally
        {
            _moduleBindingStack.Remove(resolvedFullPath);
        }

        _moduleDefinitionCache[resolvedFullPath] = program;
        return program;
    }

    private static BoundModule CreateEmptyImportedModule(string resolvedPath, CompilationUnitSyntax? syntax = null)
    {
        CompilationUnitSyntax effectiveSyntax = syntax ?? new CompilationUnitSyntax([], new Token(TokenKind.EndOfFile, new TextSpan(0, 0), string.Empty));
        return new BoundModule(
            resolvedPath,
            effectiveSyntax,
            [],
            [],
            new Dictionary<string, Symbol>());
    }

    private BoundModule GetOrCreateBuiltinModule()
    {
        if (_moduleDefinitionCache.TryGetValue(BuiltinModulePath, out BoundModule? cached))
            return cached;

        LayoutSymbol intRegsLayout = new("IntRegs");
        AddBuiltinLayoutMember(intRegsLayout, "IJMP3", isConst: false, address: 0x1F0);
        AddBuiltinLayoutMember(intRegsLayout, "IRET3", isConst: false, address: 0x1F1);
        AddBuiltinLayoutMember(intRegsLayout, "IJMP2", isConst: false, address: 0x1F2);
        AddBuiltinLayoutMember(intRegsLayout, "IRET2", isConst: false, address: 0x1F3);
        AddBuiltinLayoutMember(intRegsLayout, "IJMP1", isConst: false, address: 0x1F4);
        AddBuiltinLayoutMember(intRegsLayout, "IRET1", isConst: false, address: 0x1F5);

        LayoutSymbol ioLayout = new("IO");
        AddBuiltinLayoutMember(ioLayout, "DIRA", isConst: false, address: 0x1FA);
        AddBuiltinLayoutMember(ioLayout, "DIRB", isConst: false, address: 0x1FB);
        AddBuiltinLayoutMember(ioLayout, "OUTA", isConst: false, address: 0x1FC);
        AddBuiltinLayoutMember(ioLayout, "OUTB", isConst: false, address: 0x1FD);
        AddBuiltinLayoutMember(ioLayout, "INA", isConst: true, address: 0x1FE);
        AddBuiltinLayoutMember(ioLayout, "INB", isConst: true, address: 0x1FF);

        LayoutSymbol sfrLayout = new("SFR");
        sfrLayout.SetParents([ioLayout, intRegsLayout]);
        AddBuiltinLayoutMember(sfrLayout, "PA", isConst: false, address: 0x1F6);
        AddBuiltinLayoutMember(sfrLayout, "PB", isConst: false, address: 0x1F7);
        AddBuiltinLayoutMember(sfrLayout, "PTRA", isConst: false, address: 0x1F8);
        AddBuiltinLayoutMember(sfrLayout, "PTRB", isConst: false, address: 0x1F9);

        Dictionary<string, Symbol> exportedSymbols = new(StringComparer.Ordinal)
        {
            ["MemorySpace"] = new TypeSymbol("MemorySpace", MemorySpaceType),
            ["IntRegs"] = intRegsLayout,
            ["IO"] = ioLayout,
            ["SFR"] = sfrLayout,
        };

        CompilationUnitSyntax emptySyntax = new([], new Token(TokenKind.EndOfFile, new TextSpan(0, 0), string.Empty));
        BoundModule builtinModule = new(
            BuiltinModulePath,
            emptySyntax,
            [],
            [],
            exportedSymbols);

        _moduleDefinitionCache[BuiltinModulePath] = builtinModule;
        return builtinModule;
    }

    private static void AddBuiltinLayoutMember(LayoutSymbol layout, string name, bool isConst, int address)
    {
        GlobalVariableSymbol member = new(
            name,
            BuiltinTypes.U32,
            isConst,
            VariableStorageClass.Cog,
            layout,
            isExtern: true,
            fixedAddress: address,
            alignment: null);
        bool added = layout.TryDeclareMember(member);
        Assert.Invariant(added, $"Builtin layout '{layout.Name}' must not contain duplicate member '{name}'.");
    }

    private bool TryDeclareSymbol(Scope scope, Symbol symbol, TextSpan span, bool preserveLocalBindingOnShadowing = false)
    {
        if (scope.TryDeclare(symbol))
            return true;

        _diagnostics.Report(new SymbolAlreadyDeclaredError(_diagnostics.CurrentSource, span, symbol.Name));

        if (preserveLocalBindingOnShadowing && !scope.ContainsInCurrentScope(symbol.Name))
            scope.DeclareInCurrentScope(symbol);

        return false;
    }

    private SourceSpan CreateSourceSpan(TextSpan span)
    {
        if (_currentSource is null)
            return SourceSpan.Synthetic();

        return new SourceSpan(_currentSource, span);
    }

    private Dictionary<string, Symbol> CreateExportedSymbols()
    {
        Dictionary<string, Symbol> exportedSymbols = new(StringComparer.Ordinal);
        foreach (string name in _globalScope.GetDeclaredNames())
        {
            bool found = _globalScope.TryLookup(name, out Symbol? symbol);
            Assert.Invariant(found && symbol is not null, $"Global scope symbol '{name}' must be retrievable.");
            exportedSymbols.Add(name, symbol);
        }

        return exportedSymbols;
    }

    private static BlockStatementSyntax CreateSyntheticBlockStatement(IReadOnlyList<StatementSyntax> statements)
    {
        Requires.NotNull(statements);

        TextSpan blockSpan = statements.Count > 0
            ? TextSpan.FromBounds(statements[0].Span.Start, statements[^1].Span.End)
            : new TextSpan(0, 0);

        Token openBrace = new(TokenKind.OpenBrace, new TextSpan(blockSpan.Start, 0), string.Empty);
        Token closeBrace = new(TokenKind.CloseBrace, new TextSpan(blockSpan.End, 0), string.Empty);
        return new BlockStatementSyntax(openBrace, statements, closeBrace);
    }

    private void CollectTopLevelTypes(CompilationUnitSyntax unit)
    {
        foreach (MemberSyntax member in unit.Members)
        {
            if (member is not TypeAliasDeclarationSyntax typeAlias)
                continue;

            TypeSymbol symbol = new(typeAlias.Name.Text, sourceSpan: CreateSourceSpan(typeAlias.Name.Span));
            if (!_typeAliases.TryAdd(symbol.Name, symbol))
            {
                _diagnostics.Report(new SymbolAlreadyDeclaredError(_diagnostics.CurrentSource, typeAlias.Name.Span, symbol.Name));
                continue;
            }

            _typeAliasDeclarations.Add(symbol, typeAlias);

            if (!TryDeclareSymbol(_globalScope, symbol, typeAlias.Name.Span))
            {
                _typeAliases.Remove(symbol.Name);
                _typeAliasDeclarations.Remove(symbol);
            }
        }
    }

    private void CollectTopLevelLayouts(CompilationUnitSyntax unit)
    {
        _currentScope = _globalScope;
        foreach (MemberSyntax member in unit.Members)
        {
            LayoutSymbol? layoutSymbol = null;
            TextSpan span;

            switch (member)
            {
                case LayoutDeclarationSyntax layoutDeclaration:
                    layoutSymbol = new LayoutSymbol(layoutDeclaration.Name.Text, CreateSourceSpan(layoutDeclaration.Name.Span));
                    span = layoutDeclaration.Name.Span;
                    break;

                case TaskDeclarationSyntax taskDeclaration:
                    VariableStorageClass? storageClass = MapStorageClass(taskDeclaration.StorageClassKeyword);
                    Assert.Invariant(storageClass.HasValue, "Tasks must declare an explicit storage class.");

                    FunctionSymbol entryFunction = new(
                        taskDeclaration.Name.Text,
                        taskDeclaration,
                        FunctionKind.Default,
                        isTopLevel: true,
                        storageClass: storageClass,
                        FunctionInliningPolicy.Default,
                        CreateSourceSpan(taskDeclaration.Name.Span));
                    layoutSymbol = new TaskSymbol(taskDeclaration.Name.Text, entryFunction, storageClass.Value, CreateSourceSpan(taskDeclaration.Name.Span));
                    span = taskDeclaration.Name.Span;
                    break;

                default:
                    continue;
            }

            if (!_layouts.TryAdd(layoutSymbol.Name, layoutSymbol))
            {
                _diagnostics.Report(new SymbolAlreadyDeclaredError(_diagnostics.CurrentSource, span, layoutSymbol.Name));
                continue;
            }

            _layoutDeclarations.Add(layoutSymbol, member);
            if (!TryDeclareSymbol(_globalScope, layoutSymbol, span))
            {
                _layouts.Remove(layoutSymbol.Name);
                _layoutDeclarations.Remove(layoutSymbol);
            }
        }
    }

    private void CollectTopLevelFunctions(CompilationUnitSyntax unit)
    {
        _currentScope = _globalScope;
        foreach (MemberSyntax member in unit.Members)
        {
            IFunctionSignatureSyntax syntax;
            FunctionKind kind;
            FunctionInliningPolicy inliningPolicy;

            switch (member)
            {
                case FunctionDeclarationSyntax functionDecl:
                    syntax = functionDecl;
                    (kind, inliningPolicy) = GetFunctionModifiers(functionDecl.Modifiers);
                    break;

                case AsmFunctionDeclarationSyntax asmFunctionDecl:
                    syntax = asmFunctionDecl;
                    kind = FunctionKind.Leaf;
                    inliningPolicy = FunctionInliningPolicy.Default;
                    break;

                default:
                    continue;
            }

            Assert.Invariant(!string.IsNullOrWhiteSpace(syntax.Name.Text), "Binder requires well-formed syntax. Parser errors must short-circuit before binding.");

            FunctionSymbol function = new(syntax.Name.Text, syntax, kind, isTopLevel: false, GetFunctionStorageClass(syntax), inliningPolicy, CreateSourceSpan(syntax.Name.Span));
            if (!_functions.TryAdd(function.Name, function))
            {
                _diagnostics.Report(new SymbolAlreadyDeclaredError(_diagnostics.CurrentSource, syntax.Name.Span, function.Name));
                continue;
            }

            if (!TryDeclareSymbol(_globalScope, function, syntax.Name.Span))
                _functions.Remove(function.Name);
        }
    }

    private void ResolveFunctionMetadata(IEnumerable<FunctionSymbol> functions, Scope bindingScope)
    {
        Scope previousScope = _currentScope;
        _currentScope = bindingScope;

        foreach (FunctionSymbol function in functions)
        {
            FunctionMetadataSyntax? metadata = GetFunctionMetadata(function.SignatureSyntax);
            List<LayoutSymbol> associatedLayouts = [];
            int? alignment = null;
            bool sawLayoutProperty = false;
            bool sawAlignProperty = false;

            if (metadata is not null)
            {
                foreach (FunctionMetadataPropertySyntax property in metadata.Properties)
                {
                    switch (property)
                    {
                        case FunctionLayoutPropertySyntax layoutProperty:
                            if (sawLayoutProperty)
                                _diagnostics.Report(new DuplicateFunctionLayoutMetadataWarning(_diagnostics.CurrentSource, layoutProperty.Span));

                            sawLayoutProperty = true;
                            ResolveFunctionLayouts(layoutProperty, associatedLayouts);
                            break;

                        case FunctionAlignPropertySyntax alignProperty:
                            if (sawAlignProperty)
                            {
                                _diagnostics.Report(new DuplicateFunctionAlignMetadataError(_diagnostics.CurrentSource, alignProperty.Span));
                                break;
                            }

                            sawAlignProperty = true;
                            alignment = BindFunctionAlignment(alignProperty);
                            break;
                    }
                }
            }

            function.SetMetadata(alignment, associatedLayouts);
        }

        _currentScope = previousScope;
    }

    private int? BindFunctionAlignment(FunctionAlignPropertySyntax alignProperty)
    {
        int? alignment = BindRequiredConstantInt(alignProperty.AlignClause.Alignment, alignProperty.AlignClause.Alignment.Span);
        if (alignment is null)
            return null;

        if (alignment <= 0 || !IsPowerOfTwo(alignment.Value))
        {
            _diagnostics.Report(new InvalidFunctionAlignmentError(_diagnostics.CurrentSource, alignProperty.AlignClause.Alignment.Span, alignment.Value));
            return null;
        }

        return alignment;
    }

    private void ResolveFunctionLayouts(FunctionLayoutPropertySyntax layoutProperty, ICollection<LayoutSymbol> associatedLayouts)
    {
        foreach (TypeSyntax layoutReference in layoutProperty.Layouts)
        {
            if (!TryResolveParentLayoutReference(layoutReference, out _, out _, out LayoutSymbol? resolvedLayout)
                || resolvedLayout is null)
            {
                continue;
            }

            if (resolvedLayout.IsTaskLayout)
            {
                _diagnostics.Report(new TaskLayoutNotAllowedInFunctionMetadataError(_diagnostics.CurrentSource, layoutReference.Span, resolvedLayout.Name));
                continue;
            }

            if (!associatedLayouts.Contains(resolvedLayout))
                associatedLayouts.Add(resolvedLayout);
        }
    }

    private static VariableStorageClass? GetFunctionStorageClass(IFunctionSignatureSyntax syntax)
    {
        return MapStorageClass(GetFunctionStorageClassKeyword(syntax));
    }

    private static Token? GetFunctionStorageClassKeyword(IFunctionSignatureSyntax syntax)
    {
        return syntax switch
        {
            FunctionDeclarationSyntax functionDeclaration => functionDeclaration.StorageClassKeyword,
            AsmFunctionDeclarationSyntax asmFunctionDeclaration => asmFunctionDeclaration.StorageClassKeyword,
            _ => null,
        };
    }

    private static FunctionMetadataSyntax? GetFunctionMetadata(IFunctionSignatureSyntax syntax)
    {
        return syntax switch
        {
            FunctionDeclarationSyntax functionDeclaration => functionDeclaration.Metadata,
            AsmFunctionDeclarationSyntax asmFunctionDeclaration => asmFunctionDeclaration.Metadata,
            _ => null,
        };
    }

    private void ResolveTaskSignatures()
    {
        _currentScope = _globalScope;
        foreach (LayoutSymbol layout in _layouts.Values)
        {
            if (layout is TaskSymbol task)
                ResolveFunctionSignature(task.EntryFunction);
        }
    }

    private void ResolveLayoutParents()
    {
        foreach ((LayoutSymbol layout, MemberSyntax member) in _layoutDeclarations)
        {
            SeparatedSyntaxList<TypeSyntax>? parentLayouts = member switch
            {
                LayoutDeclarationSyntax layoutDeclaration => layoutDeclaration.ParentLayouts,
                TaskDeclarationSyntax taskDeclaration => taskDeclaration.ParentLayouts,
                _ => null,
            };

            if (parentLayouts is null)
            {
                layout.SetParents([]);
                continue;
            }

            List<LayoutSymbol> resolvedParents = new();
            HashSet<string> parentNames = new(StringComparer.Ordinal);
            foreach (TypeSyntax parentLayout in parentLayouts)
            {
                if (!TryResolveParentLayoutReference(parentLayout, out string parentName, out TextSpan parentSpan, out LayoutSymbol? resolvedParent))
                {
                    continue;
                }

                if (!parentNames.Add(parentName))
                {
                    _diagnostics.Report(new SymbolAlreadyDeclaredError(_diagnostics.CurrentSource, parentSpan, parentName));
                    continue;
                }

                Assert.Invariant(resolvedParent is not null, "Parent layout resolution must provide a symbol when it succeeds.");
                if (resolvedParent.IsTaskLayout)
                {
                    _diagnostics.Report(new TaskLayoutCannotBeInheritedError(_diagnostics.CurrentSource, parentSpan, resolvedParent.Name));
                    continue;
                }

                resolvedParents.Add(Requires.NotNull(resolvedParent));
            }

            layout.SetParents(resolvedParents);
        }
    }

    private bool TryResolveParentLayoutReference(TypeSyntax parentLayout, out string parentName, out TextSpan parentSpan, out LayoutSymbol? resolvedParent)
    {
        Requires.NotNull(parentLayout);

        switch (parentLayout)
        {
            case NamedTypeSyntax namedType:
                parentName = namedType.Name.Text;
                parentSpan = namedType.Name.Span;
                if (!_layouts.TryGetValue(parentName, out resolvedParent))
                {
                    _diagnostics.Report(new UndefinedNameError(_diagnostics.CurrentSource, parentSpan, parentName));
                    return false;
                }

                return true;

            case QualifiedTypeSyntax qualifiedType:
                parentName = string.Join('.', qualifiedType.Parts.Select(static part => part.Text));
                parentSpan = qualifiedType.Span;
                if (!TryResolveQualifiedLayoutSymbol(qualifiedType, out resolvedParent))
                {
                    _diagnostics.Report(new UndefinedNameError(_diagnostics.CurrentSource, parentSpan, parentName));
                    return false;
                }

                return true;

            default:
                Assert.Unreachable($"Unexpected parent layout syntax '{parentLayout.GetType().Name}'."); // pragma: force-coverage
                parentName = string.Empty; // pragma: force-coverage
                parentSpan = parentLayout.Span; // pragma: force-coverage
                resolvedParent = null; // pragma: force-coverage
                return false; // pragma: force-coverage
        }
    }

    private bool TryResolveQualifiedLayoutSymbol(QualifiedTypeSyntax qualifiedType, out LayoutSymbol? resolvedLayout)
    {
        Token root = qualifiedType.Parts[0];
        if (!_globalScope.TryLookup(root.Text, out Symbol? symbol) || symbol is not ModuleSymbol moduleSymbol)
        {
            resolvedLayout = null;
            return false;
        }

        BoundModule module = moduleSymbol.Module;
        for (int i = 1; i < qualifiedType.Parts.Count - 1; i++)
        {
            Token segment = qualifiedType.Parts[i];
            if (!module.ExportedSymbols.TryGetValue(segment.Text, out Symbol? nestedSymbol)
                || nestedSymbol is not ModuleSymbol nestedModule)
            {
                resolvedLayout = null;
                return false;
            }

            module = nestedModule.Module;
        }

        Token finalSegment = qualifiedType.Parts[^1];
        if (!module.ExportedSymbols.TryGetValue(finalSegment.Text, out Symbol? resolvedSymbol)
            || resolvedSymbol is not LayoutSymbol layoutSymbol)
        {
            resolvedLayout = null;
            return false;
        }

        resolvedLayout = layoutSymbol;
        return true;
    }

    private void BindLayoutDeclaration(LayoutSymbol layout, LayoutDeclarationSyntax layoutDeclaration, ICollection<GlobalVariableSymbol> boundGlobals)
    {
        IReadOnlyList<StoredLayoutMemberBinding> members = CollectStoredLayoutMembers(layout, layoutDeclaration.Declarations);
        BindStoredLayoutMembers(members, bindingScope: _globalScope, implicitLayout: null, boundGlobals);
    }

    private void BindTaskDeclaration(
        TaskSymbol task,
        TaskDeclarationSyntax taskDeclaration,
        ICollection<GlobalVariableSymbol> boundGlobals,
        ICollection<BoundFunctionMember> boundFunctions)
    {
        Scope previousScope = _currentScope;
        _currentScope = new Scope(_globalScope);
        Scope taskScope = _currentScope;

        List<TypeSymbol> localTypeAliases = CollectTaskLocalTypeAliases(taskDeclaration, taskScope);
        foreach (TypeSymbol localTypeAlias in localTypeAliases)
            _ = ResolveTypeAlias(localTypeAlias, localTypeAlias.SourceSpan.Span);

        List<TaskLocalFunctionBinding> localFunctions = CollectTaskLocalFunctions(taskDeclaration, taskScope);
        task.EntryFunction.SetImplicitLayout(task);
        foreach (TaskLocalFunctionBinding localFunction in localFunctions)
            localFunction.Symbol.SetImplicitLayout(task);

        ResolveFunctionMetadata(localFunctions.Select(static localFunction => localFunction.Symbol), taskScope);
        foreach (TaskLocalFunctionBinding localFunction in localFunctions)
            ResolveFunctionSignature(localFunction.Symbol);

        IReadOnlyList<StoredLayoutMemberBinding> members = CollectStoredLayoutMembers(task, GetTaskStoredDeclarations(taskDeclaration));

        foreach (TaskLocalFunctionBinding localFunction in localFunctions)
            boundFunctions.Add(BindTaskLocalFunction(task, localFunction, taskScope));

        BindStoredLayoutMembers(members, taskScope, task, boundGlobals);
        
        boundFunctions.Add(BindTaskEntryFunction(task, taskDeclaration, taskScope));

        _currentScope = previousScope;
    }

    private List<TypeSymbol> CollectTaskLocalTypeAliases(TaskDeclarationSyntax taskDeclaration, Scope taskScope)
    {
        List<TypeSymbol> aliases = [];
        foreach (SyntaxNode item in taskDeclaration.Body.Items)
        {
            if (item is not TypeAliasDeclarationSyntax typeAlias)
                continue;

            TypeSymbol symbol = new(typeAlias.Name.Text, sourceSpan: CreateSourceSpan(typeAlias.Name.Span));
            if (!TryDeclareSymbol(taskScope, symbol, typeAlias.Name.Span))
                continue;

            _typeAliasDeclarations.Add(symbol, typeAlias);
            aliases.Add(symbol);
        }

        return aliases;
    }

    private List<TaskLocalFunctionBinding> CollectTaskLocalFunctions(TaskDeclarationSyntax taskDeclaration, Scope taskScope)
    {
        List<TaskLocalFunctionBinding> localFunctions = [];
        foreach (SyntaxNode item in taskDeclaration.Body.Items)
        {
            FunctionSymbol? symbol = item switch
            {
                FunctionDeclarationSyntax functionDeclaration => CreateTaskLocalFunctionSymbol(functionDeclaration),
                AsmFunctionDeclarationSyntax asmFunctionDeclaration => CreateTaskLocalAsmFunctionSymbol(asmFunctionDeclaration),
                _ => null,
            };

            if (symbol is null)
                continue;

            if (!TryDeclareSymbol(taskScope, symbol, symbol.SignatureNameSpan))
                continue;

            localFunctions.Add(new TaskLocalFunctionBinding(symbol, item));
        }

        return localFunctions;
    }

    private FunctionSymbol CreateTaskLocalFunctionSymbol(FunctionDeclarationSyntax functionDeclaration)
    {
        (FunctionKind kind, FunctionInliningPolicy inliningPolicy) = GetFunctionModifiers(functionDeclaration.Modifiers);
        return new FunctionSymbol(
            functionDeclaration.Name.Text,
            functionDeclaration,
            kind,
            isTopLevel: false,
            GetFunctionStorageClass(functionDeclaration),
            inliningPolicy,
            CreateSourceSpan(functionDeclaration.Name.Span));
    }

    private FunctionSymbol CreateTaskLocalAsmFunctionSymbol(AsmFunctionDeclarationSyntax asmFunctionDeclaration)
    {
        return new FunctionSymbol(
            asmFunctionDeclaration.Name.Text,
            asmFunctionDeclaration,
            FunctionKind.Leaf,
            isTopLevel: false,
            GetFunctionStorageClass(asmFunctionDeclaration),
            FunctionInliningPolicy.Default,
            CreateSourceSpan(asmFunctionDeclaration.Name.Span));
    }

    private static IReadOnlyList<VariableDeclarationSyntax> GetTaskStoredDeclarations(TaskDeclarationSyntax taskDeclaration)
    {
        List<VariableDeclarationSyntax> declarations = [];
        foreach (SyntaxNode item in taskDeclaration.Body.Items)
        {
            if (item is VariableDeclarationSyntax declaration && MapStorageClass(declaration.StorageClassKeyword) is not null)
                declarations.Add(declaration);
        }

        return declarations;
    }

    private IReadOnlyList<StoredLayoutMemberBinding> CollectStoredLayoutMembers(LayoutSymbol layout, IReadOnlyList<VariableDeclarationSyntax> declarations)
    {
        List<StoredLayoutMemberBinding> storedMembers = [];
        foreach (VariableDeclarationSyntax declaration in declarations)
        {
            Assert.NotNull(MapStorageClass(declaration.StorageClassKeyword));

            _suppressPointerStorageClassDiagnostics = true;
            BladeType variableType = BindType(declaration.Type);
            _suppressPointerStorageClassDiagnostics = false;

            GlobalVariableSymbol symbol = CreateGlobalVariableSymbol(declaration, variableType, layout);
            if (HasInheritedLayoutMember(layout, symbol.Name))
                _diagnostics.Report(new LayoutMemberShadowsParentMemberWarning(_diagnostics.CurrentSource, declaration.Name.Span, layout.Name, symbol.Name));

            if (!layout.TryDeclareMember(symbol))
            {
                _diagnostics.Report(new SymbolAlreadyDeclaredError(_diagnostics.CurrentSource, declaration.Name.Span, symbol.Name));
                continue;
            }

            storedMembers.Add(new StoredLayoutMemberBinding(declaration, symbol));
        }

        _layoutVisibleMemberCache.Clear();

        return storedMembers;
    }

    private void BindStoredLayoutMembers(
        IReadOnlyList<StoredLayoutMemberBinding> storedMembers,
        Scope bindingScope,
        LayoutSymbol? implicitLayout,
        ICollection<GlobalVariableSymbol> boundGlobals)
    {
        FunctionSymbol? activeFunction = implicitLayout is TaskSymbol task
            ? task.EntryFunction
            : null;

        using IDisposable bindContext = PushContext(
            bindingScope,
            activeFunction,
            implicitLayout,
            activeFunction is null ? [] : GetEffectiveLayouts(activeFunction, implicitLayout));

        foreach (StoredLayoutMemberBinding storedMember in storedMembers)
        {
            BindStoredLayoutMember(storedMember.Declaration, storedMember.Symbol);
            boundGlobals.Add(storedMember.Symbol);
        }
    }

    private void BindStoredLayoutMember(VariableDeclarationSyntax declaration, GlobalVariableSymbol variableSymbol)
    {
        ResolveLayoutMetadata(declaration, variableSymbol);

        BoundExpression? initializer = null;
        if (declaration.Initializer is not null)
        {
            if (variableSymbol.IsExtern)
            {
                _diagnostics.Report(new ExternCannotHaveInitializerError(_diagnostics.CurrentSource, declaration.Initializer.Span, variableSymbol.Name));
            }
            else
            {
                initializer = BindExpression(declaration.Initializer, variableSymbol.Type);
                initializer = RequireComptimeExpression(initializer, declaration.Initializer.Span);
            }
        }

        variableSymbol.SetInitializer(initializer);
    }

    private BoundFunctionMember BindTaskEntryFunction(TaskSymbol task, TaskDeclarationSyntax taskDeclaration, Scope taskScope)
    {
        List<StatementSyntax> statements = [];
        foreach (SyntaxNode item in taskDeclaration.Body.Items)
        {
            switch (item)
            {
                case StatementSyntax statement:
                    statements.Add(statement);
                    break;

                case VariableDeclarationSyntax declaration when MapStorageClass(declaration.StorageClassKeyword) is null:
                    statements.Add(new VariableDeclarationStatementSyntax(declaration));
                    break;
            }
        }

        BlockStatementSyntax bodySyntax = CreateSyntheticBlockStatement(statements);
        return BindBlockFunction(task.EntryFunction, bodySyntax, taskDeclaration.Span, bodySyntax.Span, taskScope, task);
    }

    private BoundFunctionMember BindTaskLocalFunction(TaskSymbol task, TaskLocalFunctionBinding localFunction, Scope taskScope)
    {
        return localFunction.Syntax switch
        {
            FunctionDeclarationSyntax functionDeclaration => BindBlockFunction(
                localFunction.Symbol,
                functionDeclaration.Body,
                functionDeclaration.Span,
                functionDeclaration.Body.CloseBrace.Span,
                taskScope,
                task),
            AsmFunctionDeclarationSyntax asmFunctionDeclaration => BindAsmFunction(localFunction.Symbol, asmFunctionDeclaration, taskScope, task),
            _ => Assert.UnreachableValue<BoundFunctionMember>(), // pragma: force-coverage
        };
    }

    private bool HasInheritedLayoutMember(LayoutSymbol layout, string memberName)
    {
        foreach (LayoutSymbol parent in layout.Parents)
        {
            if (GetVisibleLayoutMembers(parent).ContainsKey(memberName))
                return true;
        }

        return false;
    }

    private IReadOnlyDictionary<string, IReadOnlyList<LayoutMemberBinding>> GetVisibleLayoutMembers(LayoutSymbol layout)
    {
        return GetVisibleLayoutMembers(layout, new HashSet<LayoutSymbol>());
    }

    private IReadOnlyDictionary<string, IReadOnlyList<LayoutMemberBinding>> GetVisibleLayoutMembers(LayoutSymbol layout, HashSet<LayoutSymbol> activeLayouts)
    {
        if (_layoutVisibleMemberCache.TryGetValue(layout, out IReadOnlyDictionary<string, IReadOnlyList<LayoutMemberBinding>>? cachedMembers))
            return cachedMembers;

        if (!activeLayouts.Add(layout))
            return new Dictionary<string, IReadOnlyList<LayoutMemberBinding>>(StringComparer.Ordinal);

        Dictionary<string, List<LayoutMemberBinding>> visibleMembers = new(StringComparer.Ordinal);
        foreach (LayoutSymbol parent in layout.Parents)
        {
            IReadOnlyDictionary<string, IReadOnlyList<LayoutMemberBinding>> parentMembers = GetVisibleLayoutMembers(parent, activeLayouts);
            foreach ((string name, IReadOnlyList<LayoutMemberBinding> bindings) in parentMembers)
            {
                if (!visibleMembers.TryGetValue(name, out List<LayoutMemberBinding>? existingBindings))
                {
                    existingBindings = [];
                    visibleMembers.Add(name, existingBindings);
                }

                foreach (LayoutMemberBinding binding in bindings)
                {
                    if (existingBindings.Any(existing => ReferenceEquals(existing.Layout, binding.Layout) && ReferenceEquals(existing.Variable, binding.Variable)))
                        continue;

                    existingBindings.Add(binding);
                }
            }
        }

        foreach ((string name, GlobalVariableSymbol symbol) in layout.DeclaredMembers)
            visibleMembers[name] = [new LayoutMemberBinding(layout, symbol)];

        activeLayouts.Remove(layout);

        Dictionary<string, IReadOnlyList<LayoutMemberBinding>> frozenMembers = new(StringComparer.Ordinal);
        foreach ((string name, List<LayoutMemberBinding> bindings) in visibleMembers)
            frozenMembers.Add(name, bindings);

        _layoutVisibleMemberCache[layout] = frozenMembers;
        return frozenMembers;
    }

    private void ResolveFunctionSignatures()
    {
        _currentScope = _globalScope;
        foreach ((_, FunctionSymbol function) in _functions)
            ResolveFunctionSignature(function);
    }

    private void ResolveFunctionSignature(FunctionSymbol function)
    {
        List<ParameterVariableSymbol> parameters = new();
        HashSet<string> parameterNames = new(StringComparer.Ordinal);

        foreach (ParameterSyntax param in function.SignatureParameters)
        {
            BladeType parameterType = BindType(param.Type);
            if (param.StorageClassKeyword is Token storageClassKeyword)
                _diagnostics.Report(new InvalidParameterStorageClassError(_diagnostics.CurrentSource, storageClassKeyword.Span, storageClassKeyword.Text));

            if (!parameterNames.Add(param.Name.Text))
            {
                _diagnostics.Report(new SymbolAlreadyDeclaredError(_diagnostics.CurrentSource, param.Name.Span, param.Name.Text));
                continue;
            }

            parameters.Add(new ParameterVariableSymbol(param.Name.Text, parameterType, CreateSourceSpan(param.Name.Span)));
        }

        List<ReturnSlot> returnSlots = new();
        if (function.SignatureReturnSpec is not null)
        {
            foreach (ReturnItemSyntax returnItem in function.SignatureReturnSpec)
            {
                BladeType returnType = BindType(returnItem.Type);
                ReturnPlacement placement = ReturnPlacement.Register;
                if (returnItem.FlagAnnotation is not null)
                {
                    placement = returnItem.FlagAnnotation.Flag.Text.ToUpperInvariant() switch
                    {
                        "C" => ReturnPlacement.FlagC,
                        "Z" => ReturnPlacement.FlagZ,
                        _ => ReturnPlacement.Register,
                    };
                }

                returnSlots.Add(new ReturnSlot(returnType, placement));
            }
        }

        if (returnSlots.Count == 1 && returnSlots[0].Type is VoidTypeSymbol)
            returnSlots.Clear();

        if (returnSlots.Count > 1)
        {
            ReturnPlacement nextFlag = ReturnPlacement.FlagC;
            for (int i = 1; i < returnSlots.Count; i++)
            {
                ReturnSlot slot = returnSlots[i];
                if (slot.Placement == ReturnPlacement.Register
                    && (slot.Type is BoolTypeSymbol || slot.Type == BuiltinTypes.Bit))
                {
                    returnSlots[i] = new ReturnSlot(slot.Type, nextFlag);
                    nextFlag = nextFlag == ReturnPlacement.FlagC ? ReturnPlacement.FlagZ : nextFlag;
                }
            }
        }

        function.Parameters = parameters;
        function.ReturnSlots = returnSlots;
    }

    private void DeclareTopLevelVariables(CompilationUnitSyntax unit)
    {
        foreach (MemberSyntax member in unit.Members)
        {
            if (member is not VariableDeclarationSyntax variableDecl)
                continue;

            if (MapStorageClass(variableDecl.StorageClassKeyword) is null)
                continue;

            if (!IsSupportedPlainTopLevelGlobal(variableDecl, reportDiagnostic: false))
                continue;

            _suppressPointerStorageClassDiagnostics = true;
            BladeType variableType = BindType(variableDecl.Type);
            _suppressPointerStorageClassDiagnostics = false;
            GlobalVariableSymbol symbol = CreateGlobalVariableSymbol(variableDecl, variableType, declaringLayout: null);

            _ = TryDeclareSymbol(_globalScope, symbol, variableDecl.Name.Span);
        }
    }

    private void ResolveAllTypeAliases()
    {
        foreach ((TypeSymbol aliasSymbol, TypeAliasDeclarationSyntax aliasSyntax) in _typeAliasDeclarations)
            _ = ResolveTypeAlias(aliasSymbol.Name, aliasSyntax.Name.Span);
    }

    private GlobalVariableSymbol BindGlobalVariable(VariableDeclarationSyntax variable)
    {
        BladeType variableType = BindType(variable.Type);
        GlobalVariableSymbol variableSymbol;
        if (_globalScope.TryLookup(variable.Name.Text, out Symbol? symbol) && symbol is GlobalVariableSymbol declaredVariable)
        {
            variableSymbol = declaredVariable;
        }
        else
        {
            variableSymbol = CreateGlobalVariableSymbol(variable, variableType, declaringLayout: null);
        }

        ResolveLayoutMetadata(variable, variableSymbol);

        BoundExpression? initializer = null;
        if (variable.Initializer is not null)
        {
            if (variableSymbol.IsExtern)
            {
                _diagnostics.Report(new ExternCannotHaveInitializerError(_diagnostics.CurrentSource, variable.Initializer.Span, variableSymbol.Name));
            }
            else
            {
                initializer = BindExpression(variable.Initializer, variableSymbol.Type);
                initializer = RequireComptimeExpression(initializer, variable.Initializer.Span);
            }
        }

        variableSymbol.SetInitializer(initializer);
        return variableSymbol;
    }

    private BoundFunctionMember BindFunction(FunctionDeclarationSyntax functionSyntax)
    {
        bool found = _functions.TryGetValue(functionSyntax.Name.Text, out FunctionSymbol? function);
        Assert.Invariant(found && function is not null, "CollectTopLevelFunctions must register every function before binding.");
        return BindBlockFunction(
            Requires.NotNull(function),
            functionSyntax.Body,
            functionSyntax.Span,
            functionSyntax.Body.CloseBrace.Span,
            _globalScope,
            implicitLayout: null);
    }

    private BoundFunctionMember BindBlockFunction(
        FunctionSymbol function,
        BlockStatementSyntax bodySyntax,
        TextSpan memberSpan,
        TextSpan missingReturnSpan,
        Scope parentScope,
        LayoutSymbol? implicitLayout)
    {
        BoundBlockStatement body;
        using (IDisposable bindContext = PushContext(new Scope(parentScope), function, implicitLayout, GetEffectiveLayouts(function, implicitLayout)))
        {
            foreach (ParameterVariableSymbol parameter in function.Parameters)
                _ = TryDeclareSymbol(_currentScope, parameter, function.SignatureNameSpan, preserveLocalBindingOnShadowing: true);

            body = BindBlockStatement(bodySyntax, createScope: false);

            if (function.Kind == FunctionKind.Coro)
            {
                CoroutineExitAnalysis exitAnalysis = AnalyzeCoroutineExitPaths(body);
                if (exitAnalysis.CanReachFunctionEnd)
                    _diagnostics.Report(new ReturnFromCoroutineError(_diagnostics.CurrentSource, missingReturnSpan, function.Name));
            }

            if (function.ReturnTypes.Count > 0 && !AlwaysReturns(body))
                _diagnostics.Report(new MissingReturnValueError(_diagnostics.CurrentSource, missingReturnSpan, function.Name));
        }

        _boundFunctionBodies[function] = body;

        return new BoundFunctionMember(function, body, memberSpan);
    }

    private BoundFunctionMember BindAsmFunction(AsmFunctionDeclarationSyntax asmSyntax)
    {
        bool found = _functions.TryGetValue(asmSyntax.Name.Text, out FunctionSymbol? function);
        Assert.Invariant(found && function is not null, "CollectTopLevelFunctions must register every asm function before binding.");
        return BindAsmFunction(Requires.NotNull(function), asmSyntax, _globalScope, implicitLayout: null);
    }

    private BoundFunctionMember BindAsmFunction(
        FunctionSymbol function,
        AsmFunctionDeclarationSyntax asmSyntax,
        Scope parentScope,
        LayoutSymbol? implicitLayout)
    {
        BoundBlockStatement body;
        using (IDisposable bindContext = PushContext(new Scope(parentScope), function, implicitLayout, GetEffectiveLayouts(function, implicitLayout)))
        {
            foreach (ParameterVariableSymbol parameter in function.Parameters)
                _ = TryDeclareSymbol(_currentScope, parameter, function.SignatureNameSpan, preserveLocalBindingOnShadowing: true);

            // For asm fn, the "return" keyword is a valid binding name referencing the return value.
            // Add a synthetic variable so the validator accepts {return} references.
            // For flag-only returns (e.g. -> bool@C), no {return} variable is needed because
            // the return value lives in the flag, not in a register.
            LocalVariableSymbol? returnSymbol = null;
            bool hasRegisterReturn = function.ReturnSlots.Any(s => s.Placement == ReturnPlacement.Register);
            if (hasRegisterReturn)
            {
                BladeType firstRegisterType = function.ReturnSlots.First(s => s.Placement == ReturnPlacement.Register).Type;
                returnSymbol = new LocalVariableSymbol(
                    "return",
                    firstRegisterType,
                    isConst: false,
                    sourceSpan: CreateSourceSpan(asmSyntax.Name.Span));
                _ = TryDeclareSymbol(_currentScope, returnSymbol, asmSyntax.Name.Span, preserveLocalBindingOnShadowing: true);
            }

            AsmVolatility volatility = asmSyntax.VolatileKeyword is not null
                ? AsmVolatility.Volatile
                : AsmVolatility.NonVolatile;

            // Determine flag output from return spec placement annotations.
            InlineAsmFlagOutput? flagOutput = null;
            ReturnSlot? firstFlagSlot = function.ReturnSlots.Cast<ReturnSlot?>().FirstOrDefault(s => s!.Value.IsFlagPlaced);
            if (firstFlagSlot is not null)
            {
                flagOutput = firstFlagSlot.Value.Placement == ReturnPlacement.FlagC
                    ? InlineAsmFlagOutput.C
                    : InlineAsmFlagOutput.Z;
            }

            InlineAsmBindingResult asmResult = BindInlineAsmBody(asmSyntax.Body);

            BoundAsmStatement asmStatement = new(volatility, flagOutput, asmResult.Lines, asmResult.ReferencedSymbols, asmSyntax.Span);

            // Synthesize an implicit return after the asm body.
            // If the function has a return type, the return reads the synthetic {return} variable
            // so the register allocator correctly propagates the asm-written value.
            List<BoundStatement> bodyStatements = [asmStatement];
            if (returnSymbol is not null)
            {
                BoundExpression returnRead = new BoundSymbolExpression(returnSymbol, asmSyntax.Span, returnSymbol.Type);
                bodyStatements.Add(new BoundReturnStatement([returnRead], asmSyntax.Span));
            }
            else
            {
                bodyStatements.Add(new BoundReturnStatement([], asmSyntax.Span));
            }

            body = new BoundBlockStatement(bodyStatements, asmSyntax.Span);
        }

        _boundFunctionBodies[function] = body;

        return new BoundFunctionMember(function, body, asmSyntax.Span);
    }

    private IDisposable PushContext(
        Scope scope,
        FunctionSymbol? function,
        LayoutSymbol? implicitLayout,
        IReadOnlyList<LayoutSymbol> effectiveLayouts)
    {
        return new BindContext(this, scope, function, implicitLayout, effectiveLayouts);
    }

    private static bool AlwaysReturns(BoundStatement statement)
    {
        switch (statement)
        {
            case BoundReturnStatement:
                return true;

            case BoundBlockStatement block:
                {
                    foreach (BoundStatement child in block.Statements)
                    {
                        if (AlwaysReturns(child))
                            return true;
                    }

                    return false;
                }

            case BoundIfStatement ifStatement:
                return ifStatement.ElseBody is not null
                    && AlwaysReturns(ifStatement.ThenBody)
                    && AlwaysReturns(ifStatement.ElseBody);

            case BoundNoirqStatement noirqStatement:
                return AlwaysReturns(noirqStatement.Body);

            default:
                return false;
        }
    }

    private static CoroutineExitAnalysis AnalyzeCoroutineExitPaths(BoundStatement statement)
    {
        switch (statement)
        {
            case BoundBlockStatement block:
                {
                    bool canContinue = true;
                    bool hasExplicitReturn = false;
                    bool canBreakLoop = false;

                    foreach (BoundStatement child in block.Statements)
                    {
                        if (!canContinue)
                            break;

                        CoroutineExitAnalysis childAnalysis = AnalyzeCoroutineExitPaths(child);
                        hasExplicitReturn |= childAnalysis.HasExplicitReturn;
                        canBreakLoop |= childAnalysis.CanBreakLoop;
                        canContinue = childAnalysis.CanReachFunctionEnd;
                    }

                    return new CoroutineExitAnalysis(canContinue, hasExplicitReturn, canBreakLoop);
                }

            case BoundIfStatement ifStatement:
                {
                    CoroutineExitAnalysis thenAnalysis = AnalyzeCoroutineExitPaths(ifStatement.ThenBody);
                    if (ifStatement.ElseBody is null)
                        return new CoroutineExitAnalysis(CanReachFunctionEnd: true, thenAnalysis.HasExplicitReturn, thenAnalysis.CanBreakLoop);

                    CoroutineExitAnalysis elseAnalysis = AnalyzeCoroutineExitPaths(ifStatement.ElseBody);
                    return new CoroutineExitAnalysis(
                        thenAnalysis.CanReachFunctionEnd || elseAnalysis.CanReachFunctionEnd,
                        thenAnalysis.HasExplicitReturn || elseAnalysis.HasExplicitReturn,
                        thenAnalysis.CanBreakLoop || elseAnalysis.CanBreakLoop);
                }

            case BoundNoirqStatement noirqStatement:
                return AnalyzeCoroutineExitPaths(noirqStatement.Body);

            case BoundLoopStatement loopStatement:
                {
                    CoroutineExitAnalysis bodyAnalysis = AnalyzeCoroutineExitPaths(loopStatement.Body);
                    return new CoroutineExitAnalysis(bodyAnalysis.CanBreakLoop, bodyAnalysis.HasExplicitReturn, CanBreakLoop: false);
                }

            case BoundRepLoopStatement repLoopStatement:
                {
                    CoroutineExitAnalysis bodyAnalysis = AnalyzeCoroutineExitPaths(repLoopStatement.Body);
                    return new CoroutineExitAnalysis(bodyAnalysis.CanBreakLoop, bodyAnalysis.HasExplicitReturn, CanBreakLoop: false);
                }

            case BoundWhileStatement whileStatement:
                {
                    CoroutineExitAnalysis bodyAnalysis = AnalyzeCoroutineExitPaths(whileStatement.Body);
                    return new CoroutineExitAnalysis(CanReachFunctionEnd: true, bodyAnalysis.HasExplicitReturn, CanBreakLoop: false);
                }

            case BoundForStatement forStatement:
                {
                    CoroutineExitAnalysis bodyAnalysis = AnalyzeCoroutineExitPaths(forStatement.Body);
                    return new CoroutineExitAnalysis(CanReachFunctionEnd: true, bodyAnalysis.HasExplicitReturn, CanBreakLoop: false);
                }

            case BoundRepForStatement repForStatement:
                {
                    CoroutineExitAnalysis bodyAnalysis = AnalyzeCoroutineExitPaths(repForStatement.Body);
                    return new CoroutineExitAnalysis(CanReachFunctionEnd: true, bodyAnalysis.HasExplicitReturn, CanBreakLoop: false);
                }

            case BoundReturnStatement:
                return new CoroutineExitAnalysis(CanReachFunctionEnd: false, HasExplicitReturn: true, CanBreakLoop: false);

            case BoundYieldtoStatement:
                return new CoroutineExitAnalysis(CanReachFunctionEnd: false, HasExplicitReturn: false, CanBreakLoop: false);

            case BoundBreakStatement:
                return new CoroutineExitAnalysis(CanReachFunctionEnd: false, HasExplicitReturn: false, CanBreakLoop: true);

            case BoundContinueStatement:
            case BoundErrorStatement:
                return new CoroutineExitAnalysis(CanReachFunctionEnd: false, HasExplicitReturn: false, CanBreakLoop: false);

            default:
                return new CoroutineExitAnalysis(CanReachFunctionEnd: true, HasExplicitReturn: false, CanBreakLoop: false);
        }
    }

    private readonly record struct CoroutineExitAnalysis(bool CanReachFunctionEnd, bool HasExplicitReturn, bool CanBreakLoop);

    private bool IsTopLevelContext()
    {
        return _currentFunction is { IsTopLevel: true };
    }

    private bool IsYieldtoContextAllowed()
    {
        return IsTopLevelContext() || _currentFunction?.Kind == FunctionKind.Coro;
    }

    private BoundBlockStatement BindBlockStatement(BlockStatementSyntax block, bool createScope)
    {
        Scope? previousScope = null;
        if (createScope)
        {
            previousScope = _currentScope;
            _currentScope = new Scope(_currentScope);
        }

        List<BoundStatement> statements = new();
        foreach (StatementSyntax statement in block.Statements)
        {
            if (BindStatementNullable(statement) is BoundStatement boundStatement)
                statements.Add(boundStatement);
        }

        if (createScope && previousScope is not null)
            _currentScope = previousScope;

        return new BoundBlockStatement(statements, block.Span);
    }

    private BoundStatement BindStatement(StatementSyntax statement)
        => BindStatementNullable(statement) ?? new BoundBlockStatement([], statement.Span);

    private BoundStatement? BindStatementNullable(StatementSyntax statement)
    {
        switch (statement)
        {
            case BlockStatementSyntax block:
                return BindBlockStatement(block, createScope: true);

            case VariableDeclarationStatementSyntax variableDeclStatement:
                return BindLocalVariableDeclaration(variableDeclStatement.Declaration);

            case ExpressionStatementSyntax expressionStatement:
                {
                    BoundExpression expr = expressionStatement.Expression is SpawnExpressionSyntax spawnExpression
                        ? BindSpawnExpression(spawnExpression, requestedResultCount: 0)
                        : BindExpression(expressionStatement.Expression);
                    return new BoundExpressionStatement(expr, expressionStatement.Span);
                }

            case AssignmentStatementSyntax assignment:
                {
                    BoundAssignmentTarget target = BindAssignmentTarget(assignment.Target);
                    BoundExpression value = BindAssignmentValue(assignment, target);
                    return new BoundAssignmentStatement(target, value, assignment.Operator.Kind, assignment.Span);
                }

            case MultiAssignmentStatementSyntax multiAssignment:
                return BindMultiAssignmentStatement(multiAssignment);

            case IfStatementSyntax ifStatement:
                {
                    BoundExpression condition = BindExpression(ifStatement.Condition, BuiltinTypes.Bool);
                    BoundStatement thenBody = ifStatement.ThenBody switch
                    {
                        BlockStatementSyntax thenBlock => BindBlockStatement(thenBlock, createScope: true),
                        _ => BindStatement(ifStatement.ThenBody),
                    };
                    BoundStatement? elseBody = null;
                    if (ifStatement.ElseClause is not null)
                    {
                        elseBody = ifStatement.ElseClause.Body switch
                        {
                            BlockStatementSyntax elseBlock => BindBlockStatement(elseBlock, createScope: true),
                            _ => BindStatement(ifStatement.ElseClause.Body),
                        };
                    }

                    return new BoundIfStatement(condition, thenBody, elseBody, ifStatement.Span);
                }

            case WhileStatementSyntax whileStatement:
                {
                    BoundExpression condition = BindExpression(whileStatement.Condition, BuiltinTypes.Bool);
                    PushLoop(LoopContext.Regular);
                    BoundBlockStatement body = BindBlockStatement(whileStatement.Body, createScope: true);
                    PopLoop();
                    return new BoundWhileStatement(condition, body, whileStatement.Span);
                }

            case ForStatementSyntax forStatement:
                return BindForStatement(forStatement);

            case LoopStatementSyntax loopStatement:
                {
                    PushLoop(LoopContext.Regular);
                    BoundBlockStatement body = BindBlockStatement(loopStatement.Body, createScope: true);
                    PopLoop();
                    return new BoundLoopStatement(body, loopStatement.Span);
                }

            case RepLoopStatementSyntax repLoop:
                {
                    PushLoop(LoopContext.Rep);
                    BoundBlockStatement body = BindBlockStatement(repLoop.Body, createScope: true);
                    PopLoop();
                    return new BoundRepLoopStatement(body, repLoop.Span);
                }

            case RepForStatementSyntax repFor:
                {
                    // The iterable should be a range expression for rep for
                    BoundExpression start;
                    BoundExpression end;
                    if (repFor.Iterable is RangeExpressionSyntax range)
                    {
                        BoundRangeExpression boundRange = BindLoopRangeExpression(range);
                        start = boundRange.Start;
                        end = boundRange.End;
                        // Normalize inclusive range: end + 1 → exclusive
                        if (range.IsInclusive && end.Type is IntegerLiteralTypeSymbol or IntegerTypeSymbol or EnumTypeSymbol or BitfieldTypeSymbol)
                        {
                            end = new BoundBinaryExpression(
                                end,
                                Requires.NotNull(BoundBinaryOperator.Bind(TokenKind.Plus)),
                                new BoundLiteralExpression(new RuntimeBladeValue(BuiltinTypes.U32, 1L), range.End.Span),
                                range.End.Span,
                                end.Type);
                        }
                    }
                    else
                    {
                        // Non-range iterable — use as count (0..<count)
                        BoundExpression iterable = BindExpression(repFor.Iterable);
                        start = new BoundLiteralExpression(new RuntimeBladeValue(BuiltinTypes.U32, 0L), repFor.Iterable.Span);
                        end = iterable;
                    }

                    PushLoop(LoopContext.Rep);
                    BoundRepForStatement boundRepFor = BindRepForBody(repFor, start, end);
                    PopLoop();
                    return boundRepFor;
                }

            case NoirqStatementSyntax noirq:
                {
                    BoundBlockStatement body = BindBlockStatement(noirq.Body, createScope: true);
                    return new BoundNoirqStatement(body, noirq.Span);
                }

            case AssertStatementSyntax assertStatement:
                return BindAssertStatement(assertStatement);

            case ReturnStatementSyntax returnStatement:
                return BindReturnStatement(returnStatement);

            case BreakStatementSyntax breakStatement:
                return BindBreakOrContinueStatement(breakStatement.BreakKeyword, isBreak: true);

            case ContinueStatementSyntax continueStatement:
                return BindBreakOrContinueStatement(continueStatement.ContinueKeyword, isBreak: false);

            case YieldStatementSyntax yieldStatement:
                {
                    if (_currentFunction is null
                        || (_currentFunction.Kind is not FunctionKind.Int1
                            and not FunctionKind.Int2
                            and not FunctionKind.Int3))
                    {
                        _diagnostics.Report(new InvalidYieldUsageError(_diagnostics.CurrentSource, yieldStatement.YieldKeyword.Span));
                    }

                    return new BoundYieldStatement(yieldStatement.Span);
                }

            case YieldtoStatementSyntax yieldtoStatement:
                return BindYieldtoStatement(yieldtoStatement);

            case AsmBlockStatementSyntax asm:
                {
                    InlineAsmFlagOutput? flagOutput = null;
                    LocalVariableSymbol? outputSymbol = null;
                    if (asm.OutputBinding is not null)
                    {
                        string flag = asm.OutputBinding.FlagAnnotation?.Flag.Text ?? "";
                        if (flag is not ("C" or "Z"))
                        {
                            _diagnostics.Report(new InlineAsmInvalidFlagOutputError(_diagnostics.CurrentSource, asm.OutputBinding.Span, flag));
                        }

                        flagOutput = flag switch
                        {
                            "C" => InlineAsmFlagOutput.C,
                            "Z" => InlineAsmFlagOutput.Z,
                            _ => null,
                        };
                        BladeType outputType = BindType(asm.OutputBinding.Type);
                        outputSymbol = new LocalVariableSymbol(
                            asm.OutputBinding.Name.Text,
                            outputType,
                            isConst: false,
                            sourceSpan: CreateSourceSpan(asm.OutputBinding.Name.Span));

                        _ = TryDeclareSymbol(_currentScope, outputSymbol, asm.OutputBinding.Name.Span, preserveLocalBindingOnShadowing: true);
                    }

                    InlineAsmBindingResult asmResult = BindInlineAsmBody(asm.Body);

                    if (outputSymbol is not null
                        && asmResult.AvailableBindings.TryGetValue(outputSymbol.Name, out InlineAsmVarBindingSlot? outputSlot))
                    {
                        asmResult.ReferencedSymbols[outputSlot] = outputSymbol;
                    }

                    return new BoundAsmStatement(asm.Volatility, flagOutput, asmResult.Lines, asmResult.ReferencedSymbols, asm.Span);
                }

            default:
                return Assert.UnreachableValue<BoundStatement>("all statement syntax types are handled above"); // pragma: force-coverage
        }
    }

    private sealed class InlineAsmBindingResult
    {
        public required IReadOnlyList<InlineAsmLine> Lines { get; init; }
        public required Dictionary<InlineAsmBindingSlot, Symbol> ReferencedSymbols { get; init; }
        public required Dictionary<string, InlineAsmVarBindingSlot> AvailableBindings { get; init; }
    }

    private InlineAsmBindingResult BindInlineAsmBody(InlineAsmBodySyntax bodySyntax)
    {
        Dictionary<string, Symbol> availableSymbols = CollectInlineAsmAvailableSymbols();
        Dictionary<string, InlineAsmVarBindingSlot> availableBindings = new(StringComparer.Ordinal);
        foreach (string name in availableSymbols.Keys)
            availableBindings.Add(name, new InlineAsmVarBindingSlot(name));

        // First pass: collect label definitions.
        Dictionary<string, ControlFlowLabelSymbol> labels = new(StringComparer.OrdinalIgnoreCase);
        foreach (InlineAsmLineSyntax lineSyntax in bodySyntax.Lines)
        {
            if (lineSyntax is InlineAsmLabelLineSyntax labelLine)
            {
                string labelName = labelLine.Name.Text;
                if (!labels.ContainsKey(labelName))
                    labels.Add(labelName, new ControlFlowLabelSymbol(labelName));
            }
        }

        List<InlineAsmLine> lines = [];
        Dictionary<InlineAsmBindingSlot, Symbol> referencedSymbols = [];
        HashSet<InlineAsmVarBindingSlot> referencedVarBindings = [];
        Dictionary<int, InlineAsmTempBindingSlot> tempBindings = [];

        foreach (InlineAsmLineSyntax lineSyntax in bodySyntax.Lines)
        {
            switch (lineSyntax)
            {
                case InlineAsmCommentLineSyntax commentLine:
                    lines.Add(new InlineAsmCommentLine(commentLine.Comment));
                    break;

                case InlineAsmLabelLineSyntax labelLine:
                    {
                        ControlFlowLabelSymbol label = labels[labelLine.Name.Text];
                        lines.Add(new InlineAsmLabelLine(label, labelLine.TrailingComment));
                        break;
                    }

                case InlineAsmInstructionLineSyntax instructionLine:
                    {
                        InlineAsmInstructionLine? bound = BindInlineAsmInstruction(
                            instructionLine, bodySyntax.Span, availableBindings, labels,
                            tempBindings, referencedVarBindings);
                        if (bound is not null)
                            lines.Add(bound);
                        break;
                    }

                default:
                    Assert.Unreachable(); // pragma: force-coverage
                    break; // pragma: force-coverage
            }
        }

        AnalyzeTempReadBeforeWrite(lines, tempBindings.Values, bodySyntax.Span);

        foreach (InlineAsmVarBindingSlot binding in referencedVarBindings)
        {
            if (availableSymbols.TryGetValue(binding.PlaceholderText, out Symbol? referenced))
                referencedSymbols[binding] = referenced;
        }

        foreach (InlineAsmTempBindingSlot tempBinding in tempBindings.Values)
        {
            LocalVariableSymbol tempSymbol = new(
                $"asm%{tempBinding.TempId}",
                BuiltinTypes.U32,
                isConst: false,
                isInlineAsmTemporary: true,
                sourceSpan: CreateSourceSpan(bodySyntax.Span));
            referencedSymbols[tempBinding] = tempSymbol;
        }

        return new InlineAsmBindingResult
        {
            Lines = lines,
            ReferencedSymbols = referencedSymbols,
            AvailableBindings = availableBindings,
        };
    }

    private InlineAsmInstructionLine? BindInlineAsmInstruction(
        InlineAsmInstructionLineSyntax instructionLine,
        TextSpan blockSpan,
        IReadOnlyDictionary<string, InlineAsmVarBindingSlot> availableBindings,
        IReadOnlyDictionary<string, ControlFlowLabelSymbol> labels,
        Dictionary<int, InlineAsmTempBindingSlot> tempBindings,
        HashSet<InlineAsmVarBindingSlot> referencedVarBindings)
    {
        P2ConditionCode? condition = null;
        if (instructionLine.Condition is Token conditionToken)
        {
            if (!P2InstructionMetadata.TryParseConditionCode(conditionToken.Text, out P2ConditionCode parsedCondition))
            {
                _diagnostics.Report(new InlineAsmUnknownInstructionError(_diagnostics.CurrentSource, blockSpan, conditionToken.Text));
                return null;
            }
            condition = parsedCondition;
        }

        if (!P2InstructionMetadata.TryParseMnemonic(instructionLine.Mnemonic.Text, out P2Mnemonic mnemonic))
        {
            _diagnostics.Report(new InlineAsmUnknownInstructionError(_diagnostics.CurrentSource, blockSpan, instructionLine.Mnemonic.Text));
            return null;
        }

        List<InlineAsmOperand> operands = [];
        foreach (InlineAsmOperandSyntax operandSyntax in instructionLine.Operands)
        {
            InlineAsmOperand? operand = BindInlineAsmOperand(
                operandSyntax, blockSpan, availableBindings, labels, tempBindings, referencedVarBindings);
            if (operand is null)
                return null;
            operands.Add(operand);
        }

        if (!P2InstructionMetadata.TryGetInstructionForm(mnemonic, operands.Count, out _))
        {
            _diagnostics.Report(new InlineAsmInvalidInstructionFormError(_diagnostics.CurrentSource, blockSpan, P2InstructionMetadata.GetMnemonicText(mnemonic), operands.Count));
            return null;
        }

        P2FlagEffect? flagEffect = null;
        if (instructionLine.FlagEffect is Token flagToken)
        {
            if (!P2InstructionMetadata.TryParseFlagEffect(flagToken.Text, out P2FlagEffect parsedFlagEffect))
            {
                _diagnostics.Report(new InlineAsmUnknownInstructionError(_diagnostics.CurrentSource, blockSpan, flagToken.Text));
                return null;
            }
            flagEffect = parsedFlagEffect;
        }

        return new InlineAsmInstructionLine(condition, mnemonic, operands, flagEffect, instructionLine.TrailingComment);
    }

    private InlineAsmOperand? BindInlineAsmOperand(
        InlineAsmOperandSyntax operandSyntax,
        TextSpan blockSpan,
        IReadOnlyDictionary<string, InlineAsmVarBindingSlot> availableBindings,
        IReadOnlyDictionary<string, ControlFlowLabelSymbol> labels,
        Dictionary<int, InlineAsmTempBindingSlot> tempBindings,
        HashSet<InlineAsmVarBindingSlot> referencedVarBindings)
    {
        switch (operandSyntax)
        {
            case InlineAsmVarBindingOperandSyntax varBinding:
                {
                    string name = string.Join(".", varBinding.Path.Select(static p => p.Text));
                    if (!availableBindings.TryGetValue(name, out InlineAsmVarBindingSlot? slot))
                    {
                        _diagnostics.Report(new InlineAsmUndefinedVariableError(_diagnostics.CurrentSource, blockSpan, name));
                        return null;
                    }
                    referencedVarBindings.Add(slot);
                    return new InlineAsmBindingRefOperand(slot);
                }

            case InlineAsmTempBindingOperandSyntax tempBinding:
                {
                    int tempId = 0;
                    if (tempBinding.Number.Value is BladeValue tempValue && tempValue.TryGetInteger(out long tempLong))
                        tempId = (int)tempLong;
                    if (!tempBindings.TryGetValue(tempId, out InlineAsmTempBindingSlot? slot))
                    {
                        slot = new InlineAsmTempBindingSlot(tempId);
                        tempBindings.Add(tempId, slot);
                    }
                    return new InlineAsmBindingRefOperand(slot);
                }

            case InlineAsmImmediateOperandSyntax immediate:
                return BindInlineAsmImmediateOperand(immediate, blockSpan, labels);

            case InlineAsmCurrentAddressOperandSyntax:
                return new InlineAsmCurrentAddressOperand(InlineAsmAddressingMode.Direct);

            case InlineAsmIntegerLiteralOperandSyntax:
                _diagnostics.Report(new InlineAsmUndefinedLabelError(_diagnostics.CurrentSource, blockSpan, operandSyntax.Span.Length > 0 ? "integer" : ""));
                return null;

            case InlineAsmSymbolOperandSyntax symbol:
                return BindInlineAsmSymbolOperand(symbol.Name.Text, blockSpan, availableBindings, labels, referencedVarBindings);

            default:
                return Assert.UnreachableValue<InlineAsmOperand?>("all inline asm operand syntaxes handled"); // pragma: force-coverage
        }
    }

    private InlineAsmOperand? BindInlineAsmImmediateOperand(
        InlineAsmImmediateOperandSyntax immediate,
        TextSpan blockSpan,
        IReadOnlyDictionary<string, ControlFlowLabelSymbol> labels)
    {
        switch (immediate.Inner)
        {
            case InlineAsmIntegerLiteralOperandSyntax intLiteral:
                {
                    long value = 0;
                    if (intLiteral.Literal.Value is BladeValue literalValue && literalValue.TryGetInteger(out long parsed))
                        value = parsed;
                    if (intLiteral.Sign is Token sign && sign.Kind == TokenKind.Minus)
                        value = -value;
                    return new InlineAsmImmediateOperand(value);
                }

            case InlineAsmCurrentAddressOperandSyntax:
                return new InlineAsmCurrentAddressOperand(InlineAsmAddressingMode.Immediate);

            case InlineAsmSymbolOperandSyntax symbol:
                {
                    string name = symbol.Name.Text;
                    if (labels.TryGetValue(name, out ControlFlowLabelSymbol? label))
                        return new InlineAsmLabelOperand(label, InlineAsmAddressingMode.Immediate);

                    _diagnostics.Report(new InlineAsmUndefinedLabelError(_diagnostics.CurrentSource, blockSpan, name));
                    return null;
                }

            default:
                _diagnostics.Report(new InlineAsmUndefinedLabelError(_diagnostics.CurrentSource, blockSpan, "#"));
                return null;
        }
    }

    private InlineAsmOperand? BindInlineAsmSymbolOperand(
        string name,
        TextSpan blockSpan,
        IReadOnlyDictionary<string, InlineAsmVarBindingSlot> availableBindings,
        IReadOnlyDictionary<string, ControlFlowLabelSymbol> labels,
        HashSet<InlineAsmVarBindingSlot> referencedVarBindings)
    {
        if (labels.TryGetValue(name, out ControlFlowLabelSymbol? label))
            return new InlineAsmLabelOperand(label, InlineAsmAddressingMode.Direct);

        if (P2InstructionMetadata.TryParseSpecialRegister(name, out P2SpecialRegister register))
            return new InlineAsmSpecialRegisterOperand(register);

        if (availableBindings.TryGetValue(name, out InlineAsmVarBindingSlot? binding))
        {
            referencedVarBindings.Add(binding);
            return new InlineAsmBindingRefOperand(binding);
        }

        _diagnostics.Report(new InlineAsmUndefinedLabelError(_diagnostics.CurrentSource, blockSpan, name));
        return null;
    }

    private void AnalyzeTempReadBeforeWrite(
        IReadOnlyList<InlineAsmLine> parsedLines,
        IReadOnlyCollection<InlineAsmTempBindingSlot> tempBindings,
        TextSpan blockSpan)
    {
        if (tempBindings.Count == 0)
            return;

        HashSet<InlineAsmTempBindingSlot> tempBindingSet = new(tempBindings);
        HashSet<InlineAsmTempBindingSlot> seenBindings = [];

        foreach (InlineAsmLine line in parsedLines)
        {
            if (line is not InlineAsmInstructionLine instruction)
                continue;

            for (int operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
            {
                if (instruction.Operands[operandIndex] is not InlineAsmBindingRefOperand binding
                    || binding.Slot is not InlineAsmTempBindingSlot tempBinding
                    || !tempBindingSet.Contains(tempBinding)
                    || !seenBindings.Add(tempBinding))
                {
                    continue;
                }

                P2OperandAccess access = P2InstructionMetadata.GetOperandAccess(
                    instruction.Mnemonic,
                    instruction.Operands.Count,
                    operandIndex);
                if (access is P2OperandAccess.Read or P2OperandAccess.ReadWrite)
                    _diagnostics.Report(new InlineAsmTempReadBeforeWriteWarning(_diagnostics.CurrentSource, blockSpan, tempBinding.PlaceholderText));
            }
        }
    }

    private BoundStatement? BindAssertStatement(AssertStatementSyntax assertStatement)
    {
        if (assertStatement.CommaToken is not null && assertStatement.MessageLiteral is null)
            return new BoundErrorStatement(assertStatement.Span);

        BoundExpression condition = BindExpression(assertStatement.Condition, BuiltinTypes.Bool);

        if (condition is BoundErrorExpression)
            return new BoundErrorStatement(assertStatement.Span);

        if (!TryEvaluateAssertCondition(condition, out ComptimeResult value, out ComptimeFailure failure))
        {
            if (!ContainsErrorExpression(condition))
            {
                if (failure.Kind is ComptimeFailureKind.NotEvaluable or ComptimeFailureKind.ForbiddenSymbolAccess)
                    _diagnostics.Report(new ComptimeValueRequiredError(_diagnostics.CurrentSource, assertStatement.Condition.Span));
                else
                    ReportComptimeFailure(failure);
            }

            return new BoundErrorStatement(assertStatement.Span);
        }

        Assert.Invariant(value.TryGetBool(out bool conditionValue), "binder binds assert condition as Bool, so comptime result must be bool");

        if (conditionValue)
            return null;

        _diagnostics.ReportAssertionFailed(
            assertStatement.Span,
            assertStatement.MessageLiteral is { } messageLiteral ? DecodeUtf8Literal(messageLiteral) : null);
        return new BoundErrorStatement(assertStatement.Span);
    }


    private Dictionary<string, Symbol> CollectInlineAsmAvailableSymbols()
    {
        Dictionary<string, Symbol> availableSymbols = new(StringComparer.Ordinal);
        Scope? scope = _currentScope;
        while (scope is not null)
        {
            foreach (string name in scope.GetDeclaredNames())
            {
                if (availableSymbols.ContainsKey(name))
                    continue;

                if (scope.TryLookup(name, out Symbol? symbol) && symbol is not null)
                    availableSymbols[name] = symbol;
            }

            scope = scope.Parent;
        }

        if (_currentImplicitLayout is not null)
        {
            foreach ((string name, IReadOnlyList<LayoutMemberBinding> bindings) in GetVisibleLayoutMembers(_currentImplicitLayout))
            {
                if (bindings.Count == 1 && !availableSymbols.ContainsKey(name))
                    availableSymbols[name] = bindings[0].Variable;
            }
        }

        if (_currentFunction is not null)
        {
            foreach (ParameterVariableSymbol param in _currentFunction.Parameters)
                availableSymbols[param.Name] = param;
        }

        foreach ((string moduleAlias, BoundModule module) in _importedModules)
        {
            _ = availableSymbols.TryAdd(moduleAlias, new ModuleSymbol(moduleAlias, module));
            CollectImportedModuleSymbols(moduleAlias, module, availableSymbols);
        }

        return availableSymbols;
    }

    private static void CollectImportedModuleSymbols(string prefix, BoundModule module, IDictionary<string, Symbol> symbols)
    {
        foreach ((string name, Symbol symbol) in module.ExportedSymbols)
        {
            symbols[$"{prefix}.{name}"] = symbol;
            if (symbol is ModuleSymbol nestedModule)
                CollectImportedModuleSymbols($"{prefix}.{name}", nestedModule.Module, symbols);
        }
    }

    private BoundVariableDeclarationStatement BindLocalVariableDeclaration(VariableDeclarationSyntax declaration)
    {
        BladeType variableType = BindType(declaration.Type);
        LocalVariableSymbol variableSymbol = CreateLocalVariableSymbol(declaration, variableType);
        if (!TryDeclareSymbol(_currentScope, variableSymbol, declaration.Name.Span, preserveLocalBindingOnShadowing: true))
        {
            return new BoundVariableDeclarationStatement(variableSymbol, initializer: null, declaration.Span);
        }

        BoundExpression? initializer = null;
        if (declaration.Initializer is not null)
            initializer = BindExpression(declaration.Initializer, variableType);

        return new BoundVariableDeclarationStatement(variableSymbol, initializer, declaration.Span);
    }

    private BoundStatement BindForStatement(ForStatementSyntax forStatement)
    {
        BoundExpression iterable = forStatement.Iterable is RangeExpressionSyntax range
            ? BindLoopRangeExpression(range)
            : BindExpression(forStatement.Iterable);
        ForBindingSyntax? binding = forStatement.Binding;
        bool isArrayIteration = iterable.Type is ArrayTypeSymbol;
        bool isIntegerIteration = iterable.Type is IntegerLiteralTypeSymbol or IntegerTypeSymbol or EnumTypeSymbol or BitfieldTypeSymbol;
        bool isRangeIteration = iterable is BoundRangeExpression;

        LocalVariableSymbol? itemVariable = null;
        bool itemIsMutable = false;
        LocalVariableSymbol? indexVariable = null;

        Scope previousScope = _currentScope;
        _currentScope = new Scope(previousScope);

        if (binding is not null)
        {
            itemIsMutable = binding.Ampersand is not null;

            if (isArrayIteration)
            {
                ArrayTypeSymbol arrayType = (ArrayTypeSymbol)iterable.Type;
                BladeType elementType = arrayType.ElementType;

                // Item variable — const or mutable alias to the element
                itemVariable = new LocalVariableSymbol(
                    binding.ItemName.Text,
                    elementType,
                    isConst: !itemIsMutable,
                    sourceSpan: CreateSourceSpan(binding.ItemName.Span));
                _ = TryDeclareSymbol(_currentScope, itemVariable, binding.ItemName.Span, preserveLocalBindingOnShadowing: true);

                // Index variable — user-provided or synthetic
                string indexName = binding.IndexName?.Text ?? "__for_index";
                indexVariable = new LocalVariableSymbol(
                    indexName,
                    BuiltinTypes.U32,
                    isConst: true,
                    sourceSpan: CreateSourceSpan(binding.IndexName?.Span ?? binding.ItemName.Span));
                _ = TryDeclareSymbol(_currentScope, indexVariable, binding.IndexName?.Span ?? binding.ItemName.Span, preserveLocalBindingOnShadowing: true);
            }
            else if (isIntegerIteration)
            {
                // for(count) -> index: the binding variable is an index
                if (itemIsMutable)
                {
                    _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, binding.Ampersand!.Value.Span, "array", iterable.Type.Name));
                }

                indexVariable = new LocalVariableSymbol(
                    binding.ItemName.Text,
                    BuiltinTypes.U32,
                    isConst: true,
                    sourceSpan: CreateSourceSpan(binding.ItemName.Span));
                _ = TryDeclareSymbol(_currentScope, indexVariable, binding.ItemName.Span, preserveLocalBindingOnShadowing: true);
            }
            else if (isRangeIteration)
            {
                // for(start..<end) -> index: the binding variable is the range index
                if (itemIsMutable)
                {
                    _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, binding.Ampersand!.Value.Span, "range", iterable.Type.Name));
                }

                indexVariable = new LocalVariableSymbol(
                    binding.ItemName.Text,
                    BuiltinTypes.U32,
                    isConst: true,
                    sourceSpan: CreateSourceSpan(binding.ItemName.Span));
                _ = TryDeclareSymbol(_currentScope, indexVariable, binding.ItemName.Span, preserveLocalBindingOnShadowing: true);
            }
            else
            {
                _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, forStatement.Iterable.Span, "integer, array, or range", iterable.Type.Name));
            }
        }
        else if (isIntegerIteration || isArrayIteration)
        {
            // No binding — create synthetic index for internal counting
            indexVariable = new LocalVariableSymbol(
                "__for_index",
                BuiltinTypes.U32,
                isConst: true,
                sourceSpan: CreateSourceSpan(forStatement.Iterable.Span));
            _ = TryDeclareSymbol(_currentScope, indexVariable, forStatement.Iterable.Span, preserveLocalBindingOnShadowing: true);
        }
        else if (isRangeIteration)
        {
            _diagnostics.Report(new RangeIterationRequiresBindingError(_diagnostics.CurrentSource, forStatement.Iterable.Span));
            // Create synthetic index so lowering has something to work with
            indexVariable = new LocalVariableSymbol(
                "__for_index",
                BuiltinTypes.U32,
                isConst: true,
                sourceSpan: CreateSourceSpan(forStatement.Iterable.Span));
            _ = TryDeclareSymbol(_currentScope, indexVariable, forStatement.Iterable.Span, preserveLocalBindingOnShadowing: true);
        }
        else if (iterable.Type is not UnknownTypeSymbol)
        {
            _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, forStatement.Iterable.Span, "integer, array, or range", iterable.Type.Name));
        }

        PushLoop(LoopContext.Regular);
        BoundBlockStatement body = BindBlockStatement(forStatement.Body, createScope: false);
        PopLoop();

        _currentScope = previousScope;
        return new BoundForStatement(iterable, itemVariable, itemIsMutable, indexVariable, body, forStatement.Span);
    }

    private BoundRepForStatement BindRepForBody(RepForStatementSyntax repFor, BoundExpression start, BoundExpression end)
    {
        Scope previousScope = _currentScope;
        _currentScope = new Scope(previousScope);

        LocalVariableSymbol? variable = null;
        if (repFor.Binding is not null)
        {
            variable = new LocalVariableSymbol(
                repFor.Binding.ItemName.Text,
                BuiltinTypes.U32,
                isConst: true,
                sourceSpan: CreateSourceSpan(repFor.Binding.ItemName.Span));
            _ = TryDeclareSymbol(_currentScope, variable, repFor.Binding.ItemName.Span, preserveLocalBindingOnShadowing: true);
        }

        BoundBlockStatement body = BindBlockStatement(repFor.Body, createScope: false);

        _currentScope = previousScope;
        return new BoundRepForStatement(variable, start, end, body, repFor.Span);
    }

    private BoundStatement BindReturnStatement(ReturnStatementSyntax returnStatement)
    {
        List<BoundExpression> values = new();

        if (_currentFunction is null || IsTopLevelContext())
        {
            _diagnostics.Report(new ReturnOutsideFunctionError(_diagnostics.CurrentSource, returnStatement.ReturnKeyword.Span));
            if (returnStatement.Values is not null)
            {
                foreach (ExpressionSyntax value in returnStatement.Values)
                    values.Add(BindExpression(value));
            }

            return new BoundReturnStatement(values, returnStatement.Span);
        }

        if (_currentFunction.Kind == FunctionKind.Coro)
        {
            _diagnostics.Report(new ReturnFromCoroutineError(_diagnostics.CurrentSource, returnStatement.ReturnKeyword.Span, _currentFunction.Name));
            if (returnStatement.Values is not null)
            {
                foreach (ExpressionSyntax value in returnStatement.Values)
                    values.Add(BindExpression(value));
            }

            return new BoundReturnStatement(values, returnStatement.Span);
        }

        int expectedCount = _currentFunction.ReturnTypes.Count;
        int actualCount = returnStatement.Values?.Count ?? 0;
        if (expectedCount != actualCount)
        {
            _diagnostics.Report(new ReturnValueCountMismatchError(_diagnostics.CurrentSource, returnStatement.ReturnKeyword.Span, _currentFunction.Name, expectedCount, actualCount));
        }

        if (returnStatement.Values is not null)
        {
            int comparedCount = Math.Min(expectedCount, returnStatement.Values.Count);
            for (int i = 0; i < comparedCount; i++)
                values.Add(BindExpression(returnStatement.Values[i], _currentFunction.ReturnTypes[i]));

            for (int i = comparedCount; i < returnStatement.Values.Count; i++)
                values.Add(BindExpression(returnStatement.Values[i]));
        }

        return new BoundReturnStatement(values, returnStatement.Span);
    }

    private BoundStatement BindMultiAssignmentStatement(MultiAssignmentStatementSyntax multiAssignment)
    {
        BoundExpression rhs = multiAssignment.Value is SpawnExpressionSyntax spawnExpression
            ? BindSpawnExpression(spawnExpression, requestedResultCount: 2)
            : BindExpression(multiAssignment.Value);
        if (rhs is not BoundMultiResultProducerExpression producer)
        {
            _diagnostics.Report(new MultiAssignmentRequiresCallError(_diagnostics.CurrentSource, multiAssignment.Value.Span));
            // Bind targets anyway to get diagnostics flowing
            foreach (ExpressionSyntax target in multiAssignment.Targets)
                BindAssignmentTarget(target);
            return new BoundExpressionStatement(rhs, multiAssignment.Span);
        }

        IReadOnlyList<BladeType> returnTypes = producer.ResultTypes;
        int targetCount = multiAssignment.Targets.Count;
        if (targetCount != returnTypes.Count)
        {
            _diagnostics.Report(new MultiAssignmentTargetCountMismatchError(_diagnostics.CurrentSource, multiAssignment.Operator.Span, producer.ResultSourceName, returnTypes.Count, targetCount));
        }

        List<BoundAssignmentTarget> targets = new();
        int count = Math.Min(targetCount, returnTypes.Count);
        for (int i = 0; i < count; i++)
        {
            ExpressionSyntax targetSyntax = multiAssignment.Targets[i];
            BladeType expectedType = returnTypes[i];

            BoundAssignmentTarget target;
            if (targetSyntax is NameExpressionSyntax name && name.Name.Text == "_")
            {
                target = new BoundDiscardAssignmentTarget(targetSyntax.Span, expectedType);
            }
            else
            {
                target = BindAssignmentTarget(targetSyntax);
            }

            targets.Add(target);
        }

        // Bind remaining targets (count mismatch case)
        for (int i = count; i < targetCount; i++)
        {
            ExpressionSyntax targetSyntax = multiAssignment.Targets[i];
            if (targetSyntax is NameExpressionSyntax name && name.Name.Text == "_")
                targets.Add(new BoundDiscardAssignmentTarget(targetSyntax.Span, BuiltinTypes.Unknown));
            else
                targets.Add(BindAssignmentTarget(targetSyntax));
        }

        return new BoundMultiAssignmentStatement(targets, producer, multiAssignment.Span);
    }

    private BoundStatement BindBreakOrContinueStatement(Token keywordToken, bool isBreak)
    {
        if (_loopStack.Count == 0)
        {
            _diagnostics.Report(new InvalidLoopControlError(_diagnostics.CurrentSource, keywordToken.Span, keywordToken.Text));
        }
        else if (isBreak && _loopStack.Peek() == LoopContext.Rep)
        {
            _diagnostics.Report(new InvalidBreakInRepLoopError(_diagnostics.CurrentSource, keywordToken.Span));
        }

        return isBreak ? new BoundBreakStatement(keywordToken.Span) : new BoundContinueStatement(keywordToken.Span);
    }

    private BoundStatement BindYieldtoStatement(YieldtoStatementSyntax yieldtoStatement)
    {
        if (!IsYieldtoContextAllowed())
            _diagnostics.Report(new InvalidYieldtoUsageError(_diagnostics.CurrentSource, yieldtoStatement.YieldtoKeyword.Span));

        if (TryResolveAccessibleName(yieldtoStatement.Target.Text, yieldtoStatement.Target.Span, out Symbol? resolvedSymbol)
            && resolvedSymbol is FunctionSymbol targetFunction)
        {
            if (targetFunction.Kind != FunctionKind.Coro)
            {
                _diagnostics.Report(new InvalidYieldtoTargetError(_diagnostics.CurrentSource, yieldtoStatement.Target.Span, yieldtoStatement.Target.Text));
                _ = BindArgumentsLoose(yieldtoStatement.Arguments);
                return new BoundErrorStatement(yieldtoStatement.Span);
            }

            ValidateFunctionLayoutSubset(targetFunction, yieldtoStatement.Target.Span);

            IReadOnlyList<BoundExpression> arguments = BindCallArguments(targetFunction, yieldtoStatement.Arguments, yieldtoStatement.Target.Span);
            return new BoundYieldtoStatement(targetFunction, arguments, yieldtoStatement.Span);
        }

        _diagnostics.Report(new UndefinedNameError(_diagnostics.CurrentSource, yieldtoStatement.Target.Span, yieldtoStatement.Target.Text));
        _ = BindArgumentsLoose(yieldtoStatement.Arguments);
        return new BoundErrorStatement(yieldtoStatement.Span);
    }

    private BoundAssignmentTarget BindAssignmentTarget(ExpressionSyntax target)
    {
        switch (target)
        {
            case NameExpressionSyntax nameExpression:
                return BindNameAssignmentTarget(nameExpression);

            case MemberAccessExpressionSyntax memberAccess:
                {
                    BoundExpression receiver = BindExpression(memberAccess.Expression);
                    if (receiver.Type is ModuleTypeSymbol moduleType)
                    {
                        BoundModule module = moduleType.Module.Module;
                        if (module.ExportedSymbols.TryGetValue(memberAccess.Member.Text, out Symbol? exportedSymbol)
                            && exportedSymbol is VariableSymbol variable)
                        {
                            return new BoundSymbolAssignmentTarget(variable, target.Span, variable.Type);
                        }

                        _diagnostics.Report(new UndefinedNameError(_diagnostics.CurrentSource, memberAccess.Member.Span, memberAccess.Member.Text));
                        return new BoundSymbolAssignmentTarget(
                            new LocalVariableSymbol(
                                memberAccess.Member.Text,
                                BuiltinTypes.Unknown,
                                isConst: false,
                                sourceSpan: CreateSourceSpan(memberAccess.Member.Span)),
                            target.Span,
                            BuiltinTypes.Unknown);
                    }

                    if (receiver.Type is LayoutTypeSymbol layoutType)
                    {
                        if (TryResolveQualifiedLayoutMember(layoutType.Layout, memberAccess.Member.Text, memberAccess.Member.Span, out GlobalVariableSymbol? variable)
                            && variable is not null)
                        {
                            return new BoundSymbolAssignmentTarget(variable, target.Span, variable.Type);
                        }

                        return new BoundSymbolAssignmentTarget(
                            new LocalVariableSymbol(
                                memberAccess.Member.Text,
                                BuiltinTypes.Unknown,
                                isConst: false,
                                sourceSpan: CreateSourceSpan(memberAccess.Member.Span)),
                            target.Span,
                            BuiltinTypes.Unknown);
                    }

                    if (receiver.Type is TaskTypeSymbol taskType)
                    {
                        if (TryResolveQualifiedTaskMember(taskType.Task, memberAccess.Member.Text, memberAccess.Member.Span, out GlobalVariableSymbol? variable)
                            && variable is not null)
                        {
                            return new BoundSymbolAssignmentTarget(variable, target.Span, variable.Type);
                        }

                        return new BoundSymbolAssignmentTarget(
                            new LocalVariableSymbol(
                                memberAccess.Member.Text,
                                BuiltinTypes.Unknown,
                                isConst: false,
                                sourceSpan: CreateSourceSpan(memberAccess.Member.Span)),
                            target.Span,
                            BuiltinTypes.Unknown);
                    }

                    if (TryGetAggregateMember(receiver.Type, memberAccess.Member.Text, out AggregateMemberSymbol? member)
                        && member is not null)
                    {
                        if (member.IsBitfield)
                        {
                            BoundAssignmentTarget receiverTarget = BindAssignmentTarget(memberAccess.Expression);
                            return new BoundBitfieldAssignmentTarget(receiverTarget, receiver, member, target.Span);
                        }

                        return new BoundMemberAssignmentTarget(receiver, member, target.Span);
                    }

                    if (receiver.Type is StructTypeSymbol or UnionTypeSymbol or BitfieldTypeSymbol)
                    {
                        _diagnostics.Report(new UndefinedNameError(_diagnostics.CurrentSource, memberAccess.Member.Span, memberAccess.Member.Text));
                    }

                    return new BoundMemberAssignmentTarget(
                        receiver,
                        new AggregateMemberSymbol(memberAccess.Member.Text, BuiltinTypes.Unknown, byteOffset: 0, bitOffset: 0, bitWidth: 0, isBitfield: false),
                        target.Span);
                }

            case IndexExpressionSyntax index:
                {
                    BoundExpression expression = BindExpression(index.Expression);
                    BoundExpression indexExpr = BindExpression(index.Index);
                    if (indexExpr.Type is not IntegerLiteralTypeSymbol and not IntegerTypeSymbol and not EnumTypeSymbol and not BitfieldTypeSymbol)
                        _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, index.Index.Span, "integer", indexExpr.Type.Name));

                    BladeType type = expression.Type switch
                    {
                        ArrayTypeSymbol array => array.ElementType,
                        MultiPointerTypeSymbol pointer => pointer.PointeeType,
                        _ => BuiltinTypes.Unknown,
                    };

                    if (expression.Type is PointerTypeSymbol)
                        _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, index.Expression.Span, "array or [*]pointer", expression.Type.Name));

                    return new BoundIndexAssignmentTarget(expression, indexExpr, target.Span, type);
                }

            case PointerDerefExpressionSyntax pointerDeref:
                {
                    BoundExpression expression = BindExpression(pointerDeref.Expression);
                    BladeType type = BindPointerDerefType(pointerDeref.Expression.Span, expression.Type);
                    return new BoundPointerDerefAssignmentTarget(expression, target.Span, type);
                }

            default:
                _diagnostics.Report(new InvalidAssignmentTargetError(_diagnostics.CurrentSource, target.Span));
                return new BoundErrorAssignmentTarget(target.Span);
        }
    }

    private BoundAssignmentTarget BindNameAssignmentTarget(NameExpressionSyntax nameExpression, BladeType? expectedType = null)
    {
        if (nameExpression.Name.Text == "_")
            return new BoundDiscardAssignmentTarget(nameExpression.Span, expectedType ?? BuiltinTypes.Unknown);

        if (!TryResolveAccessibleName(nameExpression.Name.Text, nameExpression.Name.Span, out Symbol? symbol) || symbol is null)
        {
            _diagnostics.Report(new UndefinedNameError(_diagnostics.CurrentSource, nameExpression.Name.Span, nameExpression.Name.Text));
            return new BoundErrorAssignmentTarget(nameExpression.Span);
        }

        if (symbol is FunctionSymbol)
        {
            _diagnostics.Report(new InvalidAssignmentTargetError(_diagnostics.CurrentSource, nameExpression.Span));
            return new BoundErrorAssignmentTarget(nameExpression.Span);
        }

        if (symbol is VariableSymbol variable)
        {
            if (variable.IsConst)
                _diagnostics.Report(new CannotAssignToConstantError(_diagnostics.CurrentSource, nameExpression.Name.Span, nameExpression.Name.Text));
            ObserveVariableForTopLevelStoreLoadElision(variable);
            return new BoundSymbolAssignmentTarget(symbol, nameExpression.Span, variable.Type);
        }

        _diagnostics.Report(new InvalidAssignmentTargetError(_diagnostics.CurrentSource, nameExpression.Span));
        return new BoundErrorAssignmentTarget(nameExpression.Span);
    }

    private BoundExpression BindExpression(ExpressionSyntax expression, BladeType? expectedType = null)
    {
        BoundExpression bound = BindExpressionCore(expression, expectedType);
        if (expectedType is not null)
            bound = BindConversion(bound, expectedType, expression.Span, reportMismatch: true);

        return TryFoldExpression(bound, reportDiagnostics: false, out BoundExpression folded)
            ? folded
            : bound;
    }

    private BoundExpression BindExpressionCore(ExpressionSyntax expression, BladeType? expectedType)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal:
                return BindLiteralExpression(literal);

            case NameExpressionSyntax name:
                return BindNameExpression(name);

            case ParenthesizedExpressionSyntax parenthesized:
                return BindExpression(parenthesized.Expression, expectedType);

            case UnaryExpressionSyntax unary:
                return BindUnaryExpression(unary);

            case BinaryExpressionSyntax binary:
                return BindBinaryExpression(binary);

            case MemberAccessExpressionSyntax memberAccess:
                return BindMemberAccessExpression(memberAccess);

            case PointerDerefExpressionSyntax pointerDeref:
                return BindPointerDerefExpression(pointerDeref);

            case IndexExpressionSyntax index:
                return BindIndexExpression(index);

            case CallExpressionSyntax call:
                return BindCallExpression(call);

            case SpawnExpressionSyntax spawn:
                return BindSpawnExpression(spawn, requestedResultCount: 1);

            case IntrinsicCallExpressionSyntax intrinsic:
                return BindIntrinsicCallExpression(intrinsic);

            case ArrayLiteralExpressionSyntax arrayLiteral:
                return BindArrayLiteralExpression(arrayLiteral, expectedType);

            case TypedStructLiteralExpressionSyntax typedStructLiteral:
                return BindTypedStructLiteralExpression(typedStructLiteral);

            case EnumLiteralExpressionSyntax enumLiteral:
                return BindEnumLiteralExpression(enumLiteral, expectedType);

            case CastExpressionSyntax castExpression:
                return BindCastExpression(castExpression);

            case BitcastExpressionSyntax bitcastExpression:
                return BindBitcastExpression(bitcastExpression);

            case IfExpressionSyntax ifExpression:
                return BindIfExpression(ifExpression);

            case RangeExpressionSyntax range:
                return BindRangeExpression(range);

            case QueryExpressionSyntax query:
                return BindQueryExpression(query);

            default:
                return Assert.UnreachableValue<BoundExpression>("all expression syntax types are handled above"); // pragma: force-coverage
        }
    }

    private BoundExpression BindLiteralExpression(LiteralExpressionSyntax literal)
    {
        if (literal.Token.Value is not BladeValue value)
            return Assert.UnreachableValue<BoundExpression>($"Literal token '{literal.Token.Kind}' did not carry a typed value."); // pragma: force-coverage

        return new BoundLiteralExpression(value, literal.Span);
    }

    private BoundExpression BindNameExpression(NameExpressionSyntax nameExpression)
    {
        if (nameExpression.Name.Text == "_")
        {
            _diagnostics.Report(new DiscardInExpressionError(_diagnostics.CurrentSource, nameExpression.Name.Span));
            return new BoundErrorExpression(nameExpression.Span);
        }

        if (!TryResolveAccessibleName(nameExpression.Name.Text, nameExpression.Name.Span, out Symbol? symbol) || symbol is null)
        {
            _diagnostics.Report(new UndefinedNameError(_diagnostics.CurrentSource, nameExpression.Name.Span, nameExpression.Name.Text));
            return new BoundErrorExpression(nameExpression.Span);
        }

        if (symbol is VariableSymbol variable)
        {
            ObserveVariableForTopLevelStoreLoadElision(variable);
            return new BoundSymbolExpression(symbol, nameExpression.Span, variable.Type);
        }
        if (symbol is FunctionSymbol function)
            return new BoundSymbolExpression(symbol, nameExpression.Span, new FunctionTypeSymbol(function));
        if (symbol is TypeSymbol typeSymbol)
        {
            if (!typeSymbol.IsResolved)
                _ = ResolveTypeAlias(typeSymbol, nameExpression.Name.Span);

            return new BoundSymbolExpression(typeSymbol, nameExpression.Span, new TypeValueTypeSymbol(typeSymbol.Type));
        }

        if (symbol is TaskSymbol task)
            return new BoundSymbolExpression(task, nameExpression.Span, new TaskTypeSymbol(task));

        if (symbol is LayoutSymbol layout)
            return new BoundSymbolExpression(layout, nameExpression.Span, new LayoutTypeSymbol(layout));

        Assert.Invariant(symbol is ModuleSymbol, "Name expressions should only resolve to variables, parameters, functions, layouts, modules, or types.");
        ModuleSymbol module = (ModuleSymbol)Requires.NotNull(symbol as ModuleSymbol);
        return new BoundSymbolExpression(module, nameExpression.Span, new ModuleTypeSymbol(module));
    }

    private bool TryResolveAccessibleName(string name, TextSpan span, out Symbol? symbol)
    {
        Symbol? lexicalSymbol = TryGetAccessibleLexicalSymbol(name);
        IReadOnlyList<LayoutMemberBinding> layoutBindings = GetImplicitLayoutBindings(name);

        if (lexicalSymbol is VariableSymbol && layoutBindings.Count > 0)
        {
            _diagnostics.ReportLexicalNameConflictsWithLayoutMember(span, name, GetLayoutBindingNames(layoutBindings));
            symbol = lexicalSymbol;
            return true;
        }

        if (lexicalSymbol is not null)
        {
            symbol = lexicalSymbol;
            return true;
        }

        if (layoutBindings.Count == 1)
        {
            symbol = layoutBindings[0].Variable;
            return true;
        }

        if (layoutBindings.Count > 1)
        {
            _diagnostics.ReportAmbiguousLayoutMemberAccess(span, name, GetLayoutBindingNames(layoutBindings));
            symbol = null;
            return false;
        }

        symbol = null;
        return false;
    }

    private Symbol? TryGetAccessibleLexicalSymbol(string name)
    {
        if (!_currentScope.TryLookup(name, out Symbol? symbol) || symbol is null)
            return null;

        return symbol;
    }

    private bool CanAccessTaskLayout(TaskSymbol task)
    {
        return _currentImplicitLayout is not null
            && ReferenceEquals(_currentImplicitLayout, task);
    }

    private bool CanAccessQualifiedLayout(LayoutSymbol layout)
    {
        foreach (LayoutSymbol effectiveLayout in _currentEffectiveLayouts)
        {
            if (IsReachableLayout(effectiveLayout, layout))
                return true;
        }

        return false;
    }

    private static bool IsReachableLayout(LayoutSymbol root, LayoutSymbol target)
    {
        if (ReferenceEquals(root, target))
            return true;

        foreach (LayoutSymbol parent in root.Parents)
        {
            if (IsReachableLayout(parent, target))
                return true;
        }

        return false;
    }

    private bool TryResolveQualifiedTaskMember(TaskSymbol task, string memberName, TextSpan span, out GlobalVariableSymbol? symbol)
    {
        if (!CanAccessTaskLayout(task))
        {
            _diagnostics.Report(new AccessToForeignLayoutError(_diagnostics.CurrentSource, span, task.Name, memberName));
            symbol = null;
            return false;
        }

        return TryResolveQualifiedLayoutMember(task, memberName, span, out symbol);
    }

    private IReadOnlyList<LayoutMemberBinding> GetImplicitLayoutBindings(string name)
    {
        if (_currentEffectiveLayouts.Count == 0)
            return [];

        List<LayoutMemberBinding> bindings = [];
        foreach (LayoutSymbol layout in _currentEffectiveLayouts)
        {
            if (GetVisibleLayoutMembers(layout).TryGetValue(name, out IReadOnlyList<LayoutMemberBinding>? layoutBindings))
                bindings.AddRange(layoutBindings);
        }

        return bindings;
    }

    private static IReadOnlyList<LayoutSymbol> GetEffectiveLayouts(FunctionSymbol function, LayoutSymbol? implicitLayout)
    {
        List<LayoutSymbol> effectiveLayouts = [];

        if (implicitLayout is not null)
            effectiveLayouts.Add(implicitLayout);

        foreach (LayoutSymbol layout in function.AssociatedLayouts)
        {
            if (!effectiveLayouts.Contains(layout))
                effectiveLayouts.Add(layout);
        }

        return effectiveLayouts;
    }

    private static List<string> GetLayoutBindingNames(IReadOnlyList<LayoutMemberBinding> bindings)
    {
        return bindings
            .Select(static binding => binding.Layout.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsPowerOfTwo(int value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }

    private bool TryResolveQualifiedLayoutMember(LayoutSymbol layout, string memberName, TextSpan span, out GlobalVariableSymbol? symbol)
    {
        if (!CanAccessQualifiedLayout(layout))
        {
            _diagnostics.Report(new AccessToForeignLayoutError(_diagnostics.CurrentSource, span, layout.Name, memberName));
            symbol = null;
            return false;
        }

        if (!GetVisibleLayoutMembers(layout).TryGetValue(memberName, out IReadOnlyList<LayoutMemberBinding>? bindings)
            || bindings.Count == 0)
        {
            _diagnostics.Report(new UndefinedNameError(_diagnostics.CurrentSource, span, memberName));
            symbol = null;
            return false;
        }

        if (bindings.Count > 1)
        {
            _diagnostics.ReportAmbiguousLayoutMemberAccess(span, memberName, GetLayoutBindingNames(bindings));
            symbol = null;
            return false;
        }

        symbol = bindings[0].Variable;
        return true;
    }

    private void ObserveVariableForTopLevelStoreLoadElision(VariableSymbol variable)
    {
        if (_currentFunction?.IsTopLevel == false
            && variable is GlobalVariableSymbol globalVariable)
        {
            globalVariable.DisableTopLevelStoreLoadChainElision();
        }
    }

    private static void ObserveAddressTakenVariableForTopLevelStoreLoadElision(VariableSymbol variable)
    {
        if (variable is GlobalVariableSymbol globalVariable)
            globalVariable.DisableTopLevelStoreLoadChainElision();
    }

    private static void ObserveAddressTakenExpressionForTopLevelStoreLoadElision(BoundExpression expression)
    {
        switch (expression)
        {
            case BoundSymbolExpression { Symbol: VariableSymbol variable }:
                ObserveAddressTakenVariableForTopLevelStoreLoadElision(variable);
                break;

            case BoundMemberAccessExpression memberAccess:
                ObserveAddressTakenExpressionForTopLevelStoreLoadElision(memberAccess.Receiver);
                break;

            case BoundIndexExpression indexExpression:
                ObserveAddressTakenExpressionForTopLevelStoreLoadElision(indexExpression.Expression);
                break;

            case BoundConversionExpression conversion:
                ObserveAddressTakenExpressionForTopLevelStoreLoadElision(conversion.Expression);
                break;

            case BoundCastExpression cast:
                ObserveAddressTakenExpressionForTopLevelStoreLoadElision(cast.Expression);
                break;

            case BoundBitcastExpression bitcast:
                ObserveAddressTakenExpressionForTopLevelStoreLoadElision(bitcast.Expression);
                break;
        }
    }

    private BoundExpression BindUnaryExpression(UnaryExpressionSyntax unary)
    {
        BoundUnaryOperator? op = BoundUnaryOperator.Bind(unary.Operator.Kind);
        Assert.Invariant(op is not null, "Parser should only produce unary operators that the binder understands.");
        BoundUnaryOperator unaryOperator = Requires.NotNull(op);

        switch (unaryOperator.Kind)
        {
            case BoundUnaryOperatorKind.LogicalNot:
                {
                    BoundExpression operand = BindExpression(unary.Operand, BuiltinTypes.Bool);
                    return new BoundUnaryExpression(unaryOperator, operand, unary.Span, BuiltinTypes.Bool);
                }

            case BoundUnaryOperatorKind.Negation:
            case BoundUnaryOperatorKind.BitwiseNot:
            case BoundUnaryOperatorKind.UnaryPlus:
                {
                    BoundExpression operand = BindExpression(unary.Operand);
                    if (operand.Type is not IntegerLiteralTypeSymbol and not IntegerTypeSymbol and not EnumTypeSymbol and not BitfieldTypeSymbol)
                        _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, unary.Operand.Span, "integer", operand.Type.Name));
                    return new BoundUnaryExpression(
                        unaryOperator,
                        operand,
                        unary.Span,
                        operand.Type is IntegerLiteralTypeSymbol or IntegerTypeSymbol or EnumTypeSymbol or BitfieldTypeSymbol ? operand.Type : BuiltinTypes.Unknown);
                }

            case BoundUnaryOperatorKind.AddressOf:
                return BindAddressOfExpression(unary, unaryOperator);
        }

        return Assert.UnreachableValue<BoundExpression>(); // pragma: force-coverage
    }

    private BoundExpression BindAddressOfExpression(UnaryExpressionSyntax unary, BoundUnaryOperator op)
    {
        if (unary.Operand is IndexExpressionSyntax indexExpression)
            return BindAddressOfIndexedElement(unary, op, indexExpression);

        if (unary.Operand is not NameExpressionSyntax nameExpression)
        {
            _diagnostics.Report(new InvalidAddressOfTargetError(_diagnostics.CurrentSource, unary.Operand.Span));
            _ = BindExpression(unary.Operand);
            return new BoundErrorExpression(unary.Span);
        }

        if (!TryResolveAccessibleName(nameExpression.Name.Text, nameExpression.Name.Span, out Symbol? symbol) || symbol is null)
        {
            _diagnostics.Report(new UndefinedNameError(_diagnostics.CurrentSource, nameExpression.Name.Span, nameExpression.Name.Text));
            return new BoundErrorExpression(unary.Span);
        }

        if (symbol is VariableSymbol variable)
            return BindAddressOfVariable(unary, op, variable);

        _diagnostics.Report(new InvalidAddressOfTargetError(_diagnostics.CurrentSource, unary.Operand.Span));
        return new BoundErrorExpression(unary.Span);
    }

    private BoundExpression BindAddressOfIndexedElement(UnaryExpressionSyntax unary, BoundUnaryOperator op, IndexExpressionSyntax indexExpression)
    {
        BoundExpression bound = BindIndexExpression(indexExpression);
        Assert.Invariant(bound is BoundIndexExpression, "Indexed address-of should bind through the normal index-expression path.");
        BoundIndexExpression index = Requires.NotNull(bound as BoundIndexExpression);
        ObserveAddressTakenExpressionForTopLevelStoreLoadElision(index.Expression);

        if (_currentFunction?.Kind == FunctionKind.Rec && TryGetRecursiveAddressOfName(index.Expression, out string? recursiveName))
        {
            _diagnostics.Report(new AddressOfRecursiveLocalError(_diagnostics.CurrentSource, unary.Operand.Span, Requires.NotNull(recursiveName)));
            return new BoundErrorExpression(unary.Span);
        }

        if (index.Expression is BoundSymbolExpression { Symbol: ParameterVariableSymbol param } && param.Type is ArrayTypeSymbol)
        {
            _diagnostics.Report(new AddressOfParameterError(_diagnostics.CurrentSource, unary.Operand.Span, param.Name));
            return new BoundErrorExpression(unary.Span);
        }

        if (!TryGetIndexedAddressType(index.Expression, index.Type, out PointerTypeSymbol? pointerType))
            return new BoundErrorExpression(unary.Span);

        return new BoundUnaryExpression(op, index, unary.Span, Requires.NotNull(pointerType));
    }

    private BoundExpression BindAddressOfVariable(UnaryExpressionSyntax unary, BoundUnaryOperator op, VariableSymbol variable)
    {
        ObserveAddressTakenVariableForTopLevelStoreLoadElision(variable);

        if (_currentFunction?.Kind == FunctionKind.Rec && variable is AutomaticVariableSymbol)
        {
            _diagnostics.Report(new AddressOfRecursiveLocalError(_diagnostics.CurrentSource, unary.Operand.Span, variable.Name));
            return new BoundErrorExpression(unary.Span);
        }

        VariableStorageClass storageClass = variable is GlobalVariableSymbol globalVariable
            ? globalVariable.StorageClass
            : VariableStorageClass.Cog;
        BladeType pointerType = variable.Type is ArrayTypeSymbol arrayType
            ? new MultiPointerTypeSymbol(arrayType.ElementType, variable.IsConst, storageClass: storageClass)
            : new PointerTypeSymbol(variable.Type, variable.IsConst, storageClass: storageClass);
        BoundSymbolExpression operand = new(variable, unary.Operand.Span, variable.Type);
        return new BoundUnaryExpression(op, operand, unary.Span, pointerType);
    }

    private static bool TryGetRecursiveAddressOfName(BoundExpression expression, out string? name)
    {
        switch (expression)
        {
            case BoundSymbolExpression { Symbol: AutomaticVariableSymbol { Type: ArrayTypeSymbol } variable }:
                name = variable.Name;
                return true;

            default:
                name = null;
                return false;
        }
    }

    private static bool TryGetIndexedAddressType(BoundExpression expression, BladeType elementType, out PointerTypeSymbol? pointerType)
    {
        if (expression.Type is MultiPointerTypeSymbol manyPointer)
        {
            pointerType = new PointerTypeSymbol(
                elementType,
                manyPointer.IsConst,
                manyPointer.StorageClass,
                manyPointer.IsVolatile,
                manyPointer.Alignment);
            return true;
        }

        switch (expression)
        {
            case BoundSymbolExpression { Symbol: VariableSymbol variable } when variable.Type is ArrayTypeSymbol:
                {
                    VariableStorageClass storageClass = variable is GlobalVariableSymbol globalVariable
                        ? globalVariable.StorageClass
                        : VariableStorageClass.Cog;
                    pointerType = new PointerTypeSymbol(elementType, variable.IsConst, storageClass: storageClass);
                    return true;
                }

            case BoundSymbolExpression { Symbol: ParameterVariableSymbol parameter } when parameter.Type is ArrayTypeSymbol:
                pointerType = new PointerTypeSymbol(elementType, isConst: false, storageClass: VariableStorageClass.Cog);
                return true;

            default:
                pointerType = null;
                return false;
        }
    }

    private BoundExpression BindBinaryExpression(BinaryExpressionSyntax binary)
    {
        BoundBinaryOperator? op = BoundBinaryOperator.Bind(binary.Operator.Kind);
        Assert.Invariant(op is not null, "Parser should only produce binary operators that the binder understands.");
        BoundBinaryOperator binaryOperator = Requires.NotNull(op);

        BoundExpression left;
        BoundExpression right;

        if (binaryOperator.Kind is BoundBinaryOperatorKind.Equals or BoundBinaryOperatorKind.NotEquals)
        {
            bool leftIsBareEnumLiteral = binary.Left is EnumLiteralExpressionSyntax;
            bool rightIsBareEnumLiteral = binary.Right is EnumLiteralExpressionSyntax;

            if (leftIsBareEnumLiteral && !rightIsBareEnumLiteral)
            {
                right = BindExpression(binary.Right);
                left = BindExpression(binary.Left, right.Type);
            }
            else if (!leftIsBareEnumLiteral && rightIsBareEnumLiteral)
            {
                left = BindExpression(binary.Left);
                right = BindExpression(binary.Right, left.Type);
            }
            else
            {
                left = BindExpression(binary.Left);
                right = BindExpression(binary.Right);
            }
        }
        else
        {
            left = BindExpression(binary.Left);
            right = BindExpression(binary.Right);
        }

        if (binaryOperator.Kind is BoundBinaryOperatorKind.Add or BoundBinaryOperatorKind.Subtract
            && TryBindPointerArithmeticExpression(binary, binaryOperator, left, right, out BoundExpression? pointerArithmetic))
        {
            return Requires.NotNull(pointerArithmetic);
        }

        if (binaryOperator.IsComparison)
        {
            if (!IsComparable(left.Type, right.Type))
                _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, binary.Span, left.Type.Name, right.Type.Name));

            if (left.Type is IntegerLiteralTypeSymbol or IntegerTypeSymbol or EnumTypeSymbol or BitfieldTypeSymbol
                && right.Type is IntegerLiteralTypeSymbol or IntegerTypeSymbol or EnumTypeSymbol or BitfieldTypeSymbol)
            {
                BladeType numericType = BestNumericType(left.Type, right.Type);
                left = BindConversion(left, numericType, left.Span, reportMismatch: false);
                right = BindConversion(right, numericType, right.Span, reportMismatch: false);
            }

            return new BoundBinaryExpression(left, binaryOperator, right, binary.Span, BuiltinTypes.Bool);
        }

        if (binaryOperator.Kind is BoundBinaryOperatorKind.LogicalAnd or BoundBinaryOperatorKind.LogicalOr)
        {
            if (left.Type is not BoolTypeSymbol || right.Type is not BoolTypeSymbol)
                _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, binary.Span, "bool", $"{left.Type.Name}, {right.Type.Name}"));
            left = BindConversion(left, BuiltinTypes.Bool, left.Span, reportMismatch: false);
            right = BindConversion(right, BuiltinTypes.Bool, right.Span, reportMismatch: false);
            return new BoundBinaryExpression(left, binaryOperator, right, binary.Span, BuiltinTypes.Bool);
        }

        if (left.Type is not IntegerLiteralTypeSymbol and not IntegerTypeSymbol and not EnumTypeSymbol and not BitfieldTypeSymbol
            || right.Type is not IntegerLiteralTypeSymbol and not IntegerTypeSymbol and not EnumTypeSymbol and not BitfieldTypeSymbol)
        {
            _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, binary.Span, "integer", $"{left.Type.Name}, {right.Type.Name}"));
        }

        if (left.Type is EnumTypeSymbol || right.Type is EnumTypeSymbol)
        {
            _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, binary.Span, "integer", $"{left.Type.Name}, {right.Type.Name}"));
            BladeType fallbackType = left.Type is EnumTypeSymbol ? left.Type : right.Type;
            return new BoundBinaryExpression(left, binaryOperator, right, binary.Span, fallbackType);
        }

        BladeType resultType = BestNumericType(left.Type, right.Type);
        left = BindConversion(left, resultType, left.Span, reportMismatch: false);
        right = BindConversion(right, resultType, right.Span, reportMismatch: false);
        return new BoundBinaryExpression(left, binaryOperator, right, binary.Span, resultType);
    }

    private BoundExpression BindAssignmentValue(AssignmentStatementSyntax assignment, BoundAssignmentTarget target)
    {
        if (target.Type is MultiPointerTypeSymbol pointerType)
        {
            return assignment.Operator.Kind switch
            {
                TokenKind.Equal => BindExpression(assignment.Value, target.Type),
                TokenKind.PlusEqual => BindPointerDeltaExpression(assignment.Value, assignment.Operator.Span, operatorText: "+="),
                TokenKind.MinusEqual => BindPointerDeltaExpression(assignment.Value, assignment.Operator.Span, operatorText: "-="),
                _ => BindInvalidPointerAssignmentValue(assignment.Value, assignment.Operator.Span, SyntaxFacts.GetText(assignment.Operator.Kind) ?? assignment.Operator.Text),
            };
        }

        return BindExpression(assignment.Value, target.Type);
    }

    private BoundExpression BindInvalidPointerAssignmentValue(ExpressionSyntax valueSyntax, TextSpan span, string operatorText)
    {
        _ = BindExpression(valueSyntax);
        _diagnostics.Report(new InvalidPointerArithmeticError(_diagnostics.CurrentSource, span, operatorText));
        return new BoundErrorExpression(span);
    }

    private bool TryBindPointerArithmeticExpression(
        BinaryExpressionSyntax binary,
        BoundBinaryOperator binaryOperator,
        BoundExpression left,
        BoundExpression right,
        out BoundExpression? bound)
    {
        bound = null;

        bool leftIsPointer = left.Type is PointerLikeTypeSymbol;
        bool rightIsPointer = right.Type is PointerLikeTypeSymbol;
        if (!leftIsPointer && !rightIsPointer)
            return false;

        string operatorText = SyntaxFacts.GetText(binary.Operator.Kind) ?? binary.Operator.Text;

        if (left.Type is not MultiPointerTypeSymbol leftPointer)
        {
            _diagnostics.Report(new InvalidPointerArithmeticError(_diagnostics.CurrentSource, binary.Span, operatorText));
            bound = new BoundErrorExpression(binary.Span);
            return true;
        }

        if (binaryOperator.Kind == BoundBinaryOperatorKind.Add)
        {
            if (rightIsPointer)
            {
                _diagnostics.Report(new InvalidPointerArithmeticError(_diagnostics.CurrentSource, binary.Span, operatorText));
                bound = new BoundErrorExpression(binary.Span);
                return true;
            }

            BoundExpression delta = BindPointerDeltaExpression(right, binary.Span, operatorText);
            bound = delta is BoundErrorExpression
                ? new BoundErrorExpression(binary.Span)
                : new BoundBinaryExpression(left, binaryOperator, delta, binary.Span, left.Type);
            return true;
        }

        if (!rightIsPointer)
        {
            BoundExpression delta = BindPointerDeltaExpression(right, binary.Span, operatorText);
            bound = delta is BoundErrorExpression
                ? new BoundErrorExpression(binary.Span)
                : new BoundBinaryExpression(left, binaryOperator, delta, binary.Span, left.Type);
            return true;
        }

        if (right.Type is not MultiPointerTypeSymbol rightPointer)
        {
            _diagnostics.Report(new InvalidPointerArithmeticError(_diagnostics.CurrentSource, binary.Span, operatorText));
            bound = new BoundErrorExpression(binary.Span);
            return true;
        }

        if (!AreCompatiblePointerSubtractionOperands(leftPointer, rightPointer))
        {
            _diagnostics.Report(new IncompatiblePointerSubtractionError(_diagnostics.CurrentSource, binary.Span, left.Type.Name, right.Type.Name));
            bound = new BoundErrorExpression(binary.Span);
            return true;
        }

        bound = new BoundBinaryExpression(left, binaryOperator, right, binary.Span, BuiltinTypes.I32);
        return true;
    }

    private BoundExpression BindPointerDeltaExpression(ExpressionSyntax expressionSyntax, TextSpan span, string operatorText)
    {
        BoundExpression expression = BindExpression(expressionSyntax);
        return BindPointerDeltaExpression(expression, span, operatorText);
    }

    private BoundExpression BindPointerDeltaExpression(BoundExpression expression, TextSpan span, string operatorText)
    {
        if (expression.Type is UnknownTypeSymbol)
            return expression;

        if (expression.Type is PointerLikeTypeSymbol
            || expression.Type is not IntegerLiteralTypeSymbol and not IntegerTypeSymbol and not EnumTypeSymbol and not BitfieldTypeSymbol)
        {
            _diagnostics.Report(new InvalidPointerArithmeticError(_diagnostics.CurrentSource, span, operatorText));
            return new BoundErrorExpression(span);
        }

        return BindConversion(expression, BuiltinTypes.U32, span, reportMismatch: false);
    }

    private BoundExpression BindMemberAccessExpression(MemberAccessExpressionSyntax memberAccess)
    {
        BoundExpression receiver = BindExpression(memberAccess.Expression);

        if (receiver.Type is LayoutTypeSymbol layoutType)
        {
            if (TryResolveQualifiedLayoutMember(layoutType.Layout, memberAccess.Member.Text, memberAccess.Member.Span, out GlobalVariableSymbol? variable)
                && variable is not null)
            {
                ObserveVariableForTopLevelStoreLoadElision(variable);
                return new BoundSymbolExpression(variable, memberAccess.Span, variable.Type);
            }

            return new BoundErrorExpression(memberAccess.Span);
        }

        if (receiver.Type is TaskTypeSymbol taskType)
        {
            if (TryResolveQualifiedTaskMember(taskType.Task, memberAccess.Member.Text, memberAccess.Member.Span, out GlobalVariableSymbol? variable)
                && variable is not null)
            {
                ObserveVariableForTopLevelStoreLoadElision(variable);
                return new BoundSymbolExpression(variable, memberAccess.Span, variable.Type);
            }

            return new BoundErrorExpression(memberAccess.Span);
        }

        if (TryGetAggregateMember(receiver.Type, memberAccess.Member.Text, out AggregateMemberSymbol? member)
            && member is not null)
        {
            return new BoundMemberAccessExpression(receiver, member, memberAccess.Span);
        }

        if (receiver.Type is TypeValueTypeSymbol typeValue && typeValue.ReferencedType is EnumTypeSymbol enumType)
        {
            return BindQualifiedEnumMember(memberAccess, enumType);
        }

        if (receiver.Type is ModuleTypeSymbol moduleType)
        {
            BoundModule module = moduleType.Module.Module;
            if (module.ExportedSymbols.TryGetValue(memberAccess.Member.Text, out Symbol? exportedSymbol))
            {
                switch (exportedSymbol)
                {
                    case FunctionSymbol function:
                        return new BoundSymbolExpression(function, memberAccess.Span, new FunctionTypeSymbol(function));

                    case VariableSymbol variable:
                        ObserveVariableForTopLevelStoreLoadElision(variable);
                        return new BoundSymbolExpression(variable, memberAccess.Span, variable.Type);

                    case TypeSymbol exportedType:
                        if (!exportedType.IsResolved)
                            _ = ResolveTypeAlias(exportedType, memberAccess.Member.Span);
                        return new BoundSymbolExpression(exportedType, memberAccess.Span, new TypeValueTypeSymbol(exportedType.Type));

                    case TaskSymbol exportedTask:
                        return new BoundSymbolExpression(exportedTask, memberAccess.Span, new TaskTypeSymbol(exportedTask));

                    case LayoutSymbol exportedLayout:
                        return new BoundSymbolExpression(exportedLayout, memberAccess.Span, new LayoutTypeSymbol(exportedLayout));

                    case ModuleSymbol nestedModule:
                        return new BoundSymbolExpression(nestedModule, memberAccess.Span, new ModuleTypeSymbol(nestedModule));
                }
            }

            _diagnostics.Report(new UndefinedNameError(_diagnostics.CurrentSource, memberAccess.Member.Span, memberAccess.Member.Text));
            return new BoundErrorExpression(memberAccess.Span);
        }

        if (receiver.Type is StructTypeSymbol or UnionTypeSymbol or BitfieldTypeSymbol)
            _diagnostics.Report(new UndefinedNameError(_diagnostics.CurrentSource, memberAccess.Member.Span, memberAccess.Member.Text));

        return new BoundMemberAccessExpression(
            receiver,
            new AggregateMemberSymbol(memberAccess.Member.Text, BuiltinTypes.Unknown, byteOffset: 0, bitOffset: 0, bitWidth: 0, isBitfield: false),
            memberAccess.Span);
    }

    private BoundExpression BindPointerDerefExpression(PointerDerefExpressionSyntax pointerDeref)
    {
        BoundExpression expression = BindExpression(pointerDeref.Expression);
        BladeType type = BindPointerDerefType(pointerDeref.Expression.Span, expression.Type);
        return new BoundPointerDerefExpression(expression, pointerDeref.Span, type);
    }

    private BladeType BindPointerDerefType(TextSpan span, BladeType expressionType)
    {
        if (expressionType is PointerTypeSymbol pointerType)
            return pointerType.PointeeType;

        _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, span, "pointer", expressionType.Name));
        return BuiltinTypes.Unknown;
    }

    private BoundExpression BindIndexExpression(IndexExpressionSyntax indexExpression)
    {
        BoundExpression expression = BindExpression(indexExpression.Expression);
        BoundExpression index = BindExpression(indexExpression.Index);
        if (index.Type is not IntegerLiteralTypeSymbol and not IntegerTypeSymbol and not EnumTypeSymbol and not BitfieldTypeSymbol)
            _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, indexExpression.Index.Span, "integer", index.Type.Name));

        BladeType type = expression.Type switch
        {
            ArrayTypeSymbol array => array.ElementType,
            MultiPointerTypeSymbol pointer => pointer.PointeeType,
            _ => BuiltinTypes.Unknown,
        };

        if (expression.Type is PointerTypeSymbol)
            _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, indexExpression.Expression.Span, "array or [*]pointer", expression.Type.Name));

        return new BoundIndexExpression(expression, index, indexExpression.Span, type);
    }

    private BoundExpression BindEnumLiteralExpression(EnumLiteralExpressionSyntax enumLiteral, BladeType? expectedType)
    {
        if (expectedType is not EnumTypeSymbol enumType)
        {
            _diagnostics.Report(new EnumLiteralRequiresContextError(_diagnostics.CurrentSource, enumLiteral.Span, enumLiteral.MemberName.Text));
            return new BoundErrorExpression(enumLiteral.Span);
        }

        if (!enumType.Members.TryGetValue(enumLiteral.MemberName.Text, out long value))
        {
            _diagnostics.Report(new UndefinedNameError(_diagnostics.CurrentSource, enumLiteral.MemberName.Span, enumLiteral.MemberName.Text));
            return new BoundErrorExpression(enumLiteral.Span);
        }

        return new BoundEnumLiteralExpression(enumType, enumLiteral.MemberName.Text, value, enumLiteral.Span);
    }

    private BoundExpression BindQualifiedEnumMember(MemberAccessExpressionSyntax memberAccess, EnumTypeSymbol enumType)
    {
        if (!enumType.Members.TryGetValue(memberAccess.Member.Text, out long value))
        {
            _diagnostics.Report(new UndefinedNameError(_diagnostics.CurrentSource, memberAccess.Member.Span, memberAccess.Member.Text));
            return new BoundErrorExpression(memberAccess.Span);
        }

        return new BoundEnumLiteralExpression(enumType, memberAccess.Member.Text, value, memberAccess.Span);
    }

    private static bool TryGetAggregateMember(BladeType type, string memberName, out AggregateMemberSymbol? member)
    {
        IReadOnlyDictionary<string, AggregateMemberSymbol>? members = type switch
        {
            StructTypeSymbol structType => structType.Members,
            UnionTypeSymbol unionType => unionType.Members,
            BitfieldTypeSymbol bitfieldType => bitfieldType.Members,
            _ => null,
        };

        if (members is not null && members.TryGetValue(memberName, out AggregateMemberSymbol? resolvedMember))
        {
            member = resolvedMember;
            return true;
        }

        member = null;
        return false;
    }

    private BoundExpression BindCallExpression(CallExpressionSyntax callExpression)
    {
        BoundExpression callee = BindExpression(callExpression.Callee);
        if (!TryGetFunctionSymbol(callee, out FunctionSymbol? maybeFunction) || maybeFunction is null)
        {
            _diagnostics.Report(new NotCallableError(_diagnostics.CurrentSource, callExpression.Callee.Span, callee.Type.Name));
            _ = BindArgumentsLoose(callExpression.Arguments);
            return new BoundErrorExpression(callExpression.Span);
        }

        FunctionSymbol function = maybeFunction;
        ValidateFunctionLayoutSubset(function, callExpression.Callee.Span);
        List<BoundExpression> arguments = BindCallArguments(function, callExpression.Arguments, callExpression.Callee.Span);
        BladeType returnType = function.ReturnTypes.Count switch
        {
            0 => BuiltinTypes.Void,
            1 => function.ReturnTypes[0],
            _ => BuiltinTypes.Unknown,
        };

        BoundCallExpression call = new(function, arguments, callExpression.Span, returnType);
        if (function.Kind == FunctionKind.Comptime)
        {
            if (TryFoldExpression(call, reportDiagnostics: true, out BoundExpression folded))
                return folded;

            return new BoundErrorExpression(callExpression.Span);
        }

        return call;
    }

    private BoundExpression BindSpawnExpression(SpawnExpressionSyntax spawnExpression, int requestedResultCount)
    {
        BoundExpression target = BindExpression(spawnExpression.Target);
        if (target is not BoundSymbolExpression { Symbol: TaskSymbol task })
        {
            if (target is not BoundErrorExpression)
                _diagnostics.Report(new InvalidSpawnTargetError(_diagnostics.CurrentSource, spawnExpression.Target.Span, GetSpawnTargetDisplayName(spawnExpression.Target)));

            if (spawnExpression.Argument is not null)
                _ = BindExpression(spawnExpression.Argument);

            return new BoundErrorExpression(spawnExpression.Span);
        }

        IReadOnlyList<BoundExpression> arguments = BindSpawnArguments(task, spawnExpression);

        BladeType resultType = requestedResultCount switch
        {
            0 => BuiltinTypes.Void,
            1 => BuiltinTypes.U32,
            2 => BuiltinTypes.Unknown,
            _ => Assert.UnreachableValue<BladeType>("spawn expressions only support 0, 1, or 2 requested results"), // pragma: force-coverage
        };

        BoundSpawnOperatorKind operatorKind = spawnExpression.Keyword.Kind == TokenKind.SpawnpairKeyword
            ? BoundSpawnOperatorKind.SpawnPair
            : BoundSpawnOperatorKind.Spawn;

        return new BoundSpawnExpression(operatorKind, task, arguments, requestedResultCount, spawnExpression.Span, resultType);
    }

    private IReadOnlyList<BoundExpression> BindSpawnArguments(TaskSymbol task, SpawnExpressionSyntax spawnExpression)
    {
        IReadOnlyList<ParameterVariableSymbol> parameters = task.EntryFunction.Parameters;
        int expectedCount = parameters.Count;
        int actualCount = spawnExpression.Argument is null ? 0 : 1;
        if (expectedCount != actualCount)
            _diagnostics.Report(new ArgumentCountMismatchError(_diagnostics.CurrentSource, spawnExpression.OpenParen.Span, task.Name, expectedCount, actualCount));

        if (spawnExpression.Argument is null)
            return [];

        if (parameters.Count == 1)
            return [BindExpression(spawnExpression.Argument, parameters[0].Type)];

        return [BindExpression(spawnExpression.Argument)];
    }

    private static string GetSpawnTargetDisplayName(ExpressionSyntax expression)
    {
        return expression switch
        {
            NameExpressionSyntax name => name.Name.Text,
            MemberAccessExpressionSyntax memberAccess => $"{GetSpawnTargetDisplayName(memberAccess.Expression)}.{memberAccess.Member.Text}",
            _ => "<invalid>"
        };
    }

    private void ValidateFunctionLayoutSubset(FunctionSymbol callee, TextSpan span)
    {
        IReadOnlyList<LayoutSymbol> callerLayouts = _currentEffectiveLayouts;
        IReadOnlyList<LayoutSymbol> calleeLayouts = GetEffectiveLayouts(callee, callee.ImplicitLayout);

        if (calleeLayouts.Count == 0)
            return;

        foreach (LayoutSymbol layout in calleeLayouts)
        {
            if (!callerLayouts.Contains(layout))
            {
                _diagnostics.Report(new FunctionLayoutSubsetViolationError(
                    _diagnostics.CurrentSource,
                    span,
                    _currentFunction!,
                    callee,
                    callerLayouts.ToArray(),
                    calleeLayouts.ToArray()));
                return;
            }
        }
    }

    private BoundExpression RequireComptimeExpression(BoundExpression expression, TextSpan span)
    {
        if (expression is BoundErrorExpression)
            return expression;

        if (TryEvaluateFoldedValue(expression, out ComptimeResult value, out ComptimeFailure foldFailure))
            return CreateFoldedLiteralExpression(value, expression);

        if (ContainsErrorExpression(expression))
            return expression;

        if (foldFailure.Kind == ComptimeFailureKind.NotEvaluable && RequiresSuccessfulComptimeEvaluation(expression))
        {
            ReportComptimeFailure(foldFailure);
            return new BoundErrorExpression(span);
        }

        if (TryValidateComptimeExpression(expression, out _))
            return expression;

        ReportComptimeFailure(foldFailure);
        return new BoundErrorExpression(span);
    }

    private bool TryFoldExpression(BoundExpression expression, bool reportDiagnostics, out BoundExpression folded)
    {
        if (TryEvaluateFoldedValue(expression, out ComptimeResult value, out ComptimeFailure failure))
        {
            folded = CreateFoldedLiteralExpression(value, expression);
            return true;
        }

        if (reportDiagnostics)
            ReportComptimeFailure(failure);

        folded = expression;
        return false;
    }

    private bool TryEvaluateFoldedValue(
        BoundExpression expression,
        out ComptimeResult value,
        out ComptimeFailure failure)
    {
        ComptimeEvaluator evaluator = new(
            _comptimeFuel,
            ResolveFunctionBodyForComptime,
            GetComptimeSupportResult);

        ComptimeResult rawValue = evaluator.TryEvaluateExpression(expression);
        if (rawValue.IsFailed)
        {
            Assert.Invariant(rawValue.TryGetFailure(out failure));
            value = ComptimeResult.Void;
            return false;
        }

        value = NormalizeFoldedValue(rawValue, expression.Type, expression.Span);
        if (value.IsFailed)
        {
            Assert.Invariant(value.TryGetFailure(out failure));
            value = ComptimeResult.Void;
            return false;
        }

        failure = default;
        return true;
    }

    private static ComptimeResult NormalizeFoldedValue(ComptimeResult value, BladeType targetType, TextSpan span)
    {
        if (value.IsFailed)
            return value;

        if (BladeValue.TryConvert(value.Value, targetType, out BladeValue normalizedValue) != EvaluationError.None)
            return new ComptimeResult(ComptimeFailureKind.NotEvaluable, span, $"value cannot be normalized to '{targetType.Name}'.");

        return new ComptimeResult(normalizedValue);
    }

    private static BoundLiteralExpression CreateFoldedLiteralExpression(ComptimeResult value, BoundExpression expression)
    {
        if (BladeValue.TryConvert(value.Value, expression.Type, out BladeValue normalizedValue) != EvaluationError.None)
            return Assert.UnreachableValue<BoundLiteralExpression>($"Failed to materialize folded value for '{expression.Type.Name}'."); // pragma: force-coverage

        return new BoundLiteralExpression(normalizedValue, expression.Span);
    }

    private bool TryEvaluateAssertCondition(BoundExpression expression, out ComptimeResult value, out ComptimeFailure failure)
    {
        return TryEvaluateFoldedValue(expression, out value, out failure);
    }

    private static bool TryValidateComptimeExpression(BoundExpression expression, out ComptimeFailure failure)
    {
        switch (expression)
        {
            case BoundLiteralExpression:
            case BoundEnumLiteralExpression:
                failure = default;
                return true;

            case BoundUnaryExpression unary:
                if (unary.Operator.Kind is BoundUnaryOperatorKind.AddressOf)
                {
                    failure = new ComptimeFailure(ComptimeFailureKind.UnsupportedConstruct, unary.Span, $"operator '{unary.Operator.Kind}' is not supported in a comptime-required context.");
                    return false;
                }

                return TryValidateComptimeExpression(unary.Operand, out failure);

            case BoundBinaryExpression binary:
                return TryValidateComptimeExpression(binary.Left, out failure)
                    && TryValidateComptimeExpression(binary.Right, out failure);

            case BoundArrayLiteralExpression arrayLiteral:
                foreach (BoundExpression element in arrayLiteral.Elements)
                {
                    if (!TryValidateComptimeExpression(element, out failure))
                        return false;
                }

                failure = default;
                return true;

            case BoundStructLiteralExpression structLiteral:
                foreach (BoundStructFieldInitializer field in structLiteral.Fields)
                {
                    if (!TryValidateComptimeExpression(field.Value, out failure))
                        return false;
                }

                failure = default;
                return true;

            case BoundConversionExpression conversion:
                return TryValidateComptimeExpression(conversion.Expression, out failure);

            case BoundCastExpression cast:
                return TryValidateComptimeExpression(cast.Expression, out failure);

            case BoundBitcastExpression bitcast:
                return TryValidateComptimeExpression(bitcast.Expression, out failure);

            case BoundIfExpression ifExpression:
                return TryValidateComptimeExpression(ifExpression.Condition, out failure)
                    && TryValidateComptimeExpression(ifExpression.ThenExpression, out failure)
                    && TryValidateComptimeExpression(ifExpression.ElseExpression, out failure);

            case BoundCallExpression call:
                failure = new ComptimeFailure(ComptimeFailureKind.NotEvaluable, call.Span, $"call to '{call.Function.Name}' must be folded before it can appear in a comptime-required context.");
                return false;

            case BoundSpawnExpression spawn:
                failure = new ComptimeFailure(ComptimeFailureKind.UnsupportedConstruct, spawn.Span, $"{spawn.ResultSourceName} expressions are not supported in comptime-required contexts.");
                return false;

            case BoundIntrinsicCallExpression intrinsic:
                failure = new ComptimeFailure(ComptimeFailureKind.UnsupportedConstruct, intrinsic.Span, "intrinsic calls are not supported in comptime-required contexts.");
                return false;

            case BoundMemberAccessExpression member:
                failure = new ComptimeFailure(ComptimeFailureKind.UnsupportedConstruct, member.Span, "member access is not supported in comptime-required contexts.");
                return false;

            case BoundIndexExpression index:
                failure = new ComptimeFailure(ComptimeFailureKind.UnsupportedConstruct, index.Span, "indexing is not supported in comptime-required contexts.");
                return false;

            case BoundPointerDerefExpression deref:
                failure = new ComptimeFailure(ComptimeFailureKind.UnsupportedConstruct, deref.Span, "pointer dereference is not supported in comptime-required contexts.");
                return false;

            case BoundRangeExpression range:
                return TryValidateComptimeExpression(range.Start, out failure)
                    && TryValidateComptimeExpression(range.End, out failure);

            case BoundSymbolExpression symbol:
                failure = new ComptimeFailure(ComptimeFailureKind.NotEvaluable, symbol.Span, $"symbol '{symbol.Symbol.Name}' is not compile-time evaluable in this context.");
                return false;

            default:
                failure = new ComptimeFailure(ComptimeFailureKind.UnsupportedConstruct, expression.Span, $"expression '{expression.Kind}' is not supported in a comptime-required context.");
                return false;
        }
    }

    private static bool ContainsErrorExpression(BoundExpression expression)
    {
        switch (expression)
        {
            case BoundErrorExpression:
                return true;

            case BoundUnaryExpression unary:
                return ContainsErrorExpression(unary.Operand);

            case BoundBinaryExpression binary:
                return ContainsErrorExpression(binary.Left) || ContainsErrorExpression(binary.Right);

            case BoundCallExpression call:
                foreach (BoundExpression argument in call.Arguments)
                {
                    if (ContainsErrorExpression(argument))
                        return true;
                }

                return false;

            case BoundSpawnExpression spawn:
                foreach (BoundExpression argument in spawn.Arguments)
                {
                    if (ContainsErrorExpression(argument))
                        return true;
                }

                return false;

            case BoundIntrinsicCallExpression intrinsic:
                foreach (BoundExpression argument in intrinsic.Arguments)
                {
                    if (ContainsErrorExpression(argument))
                        return true;
                }

                return false;

            case BoundArrayLiteralExpression arrayLiteral:
                foreach (BoundExpression element in arrayLiteral.Elements)
                {
                    if (ContainsErrorExpression(element))
                        return true;
                }

                return false;

            case BoundStructLiteralExpression structLiteral:
                foreach (BoundStructFieldInitializer field in structLiteral.Fields)
                {
                    if (ContainsErrorExpression(field.Value))
                        return true;
                }

                return false;

            case BoundConversionExpression conversion:
                return ContainsErrorExpression(conversion.Expression);

            case BoundCastExpression cast:
                return ContainsErrorExpression(cast.Expression);

            case BoundBitcastExpression bitcast:
                return ContainsErrorExpression(bitcast.Expression);

            case BoundIfExpression ifExpression:
                return ContainsErrorExpression(ifExpression.Condition)
                    || ContainsErrorExpression(ifExpression.ThenExpression)
                    || ContainsErrorExpression(ifExpression.ElseExpression);

            case BoundRangeExpression range:
                return ContainsErrorExpression(range.Start) || ContainsErrorExpression(range.End);

            default:
                return false;
        }
    }

    private static bool RequiresSuccessfulComptimeEvaluation(BoundExpression expression)
    {
        return expression switch
        {
            BoundLiteralExpression => true,
            BoundEnumLiteralExpression => true,
            BoundUnaryExpression => true,
            BoundBinaryExpression => true,
            BoundCallExpression => true,
            BoundIfExpression => true,
            BoundConversionExpression conversion when conversion.Type is not ArrayTypeSymbol and not StructTypeSymbol and not UnionTypeSymbol => true,
            BoundCastExpression cast when cast.Type is not ArrayTypeSymbol and not StructTypeSymbol and not UnionTypeSymbol => true,
            BoundBitcastExpression => true,
            BoundSymbolExpression => true,
            _ => false,
        };
    }

    private BoundBlockStatement ResolveFunctionBodyForComptime(FunctionSymbol function)
    {
        if (_boundFunctionBodies.TryGetValue(function, out BoundBlockStatement? localBody))
            return Requires.NotNull(localBody);

        foreach (BoundModule module in _importedModules.Values)
        {
            if (TryResolveImportedFunctionBody(module, function, out BoundBlockStatement? importedBody))
                return Requires.NotNull(importedBody);
        }

        Assert.Unreachable($"Comptime evaluation requires a resolved body for function '{function.Name}'. This indicates a binder ordering bug.");
        throw new UnreachableException();
    }

    private static bool TryResolveImportedFunctionBody(BoundModule module, FunctionSymbol function, out BoundBlockStatement? body)
    {
        foreach (BoundFunctionMember member in module.Functions)
        {
            if (ReferenceEquals(member.Symbol, function))
            {
                body = member.Body;
                return true;
            }
        }

        foreach (Symbol exportedSymbol in module.ExportedSymbols.Values)
        {
            if (exportedSymbol is ModuleSymbol nestedModule
                && TryResolveImportedFunctionBody(nestedModule.Module, function, out body))
            {
                return true;
            }
        }

        body = null;
        return false;
    }

    private ComptimeSupportResult GetComptimeSupportResult(FunctionSymbol function)
    {
        if (_comptimeSupportCache.TryGetValue(function, out ComptimeSupportResult cached))
            return cached;

        BoundBlockStatement body = ResolveFunctionBodyForComptime(function);

        ComptimeFunctionSupportAnalyzer analyzer = new();
        ComptimeSupportResult analyzed = analyzer.Analyze(function, body);
        _comptimeSupportCache[function] = analyzed;
        return analyzed;
    }

    private void ReportComptimeFailure(ComptimeFailure failure)
    {
        Assert.Invariant(!string.IsNullOrEmpty(failure.Detail), "ReportComptimeFailure should only be called with an actual failure.");

        switch (failure.Kind)
        {
            case ComptimeFailureKind.NotEvaluable:
                _diagnostics.Report(new ComptimeValueRequiredError(_diagnostics.CurrentSource, failure.Span));
                break;

            case ComptimeFailureKind.UnsupportedConstruct:
                _diagnostics.Report(new ComptimeUnsupportedConstructError(_diagnostics.CurrentSource, failure.Span, failure.Detail));
                break;

            case ComptimeFailureKind.ForbiddenSymbolAccess:
                _diagnostics.Report(new ComptimeForbiddenSymbolAccessError(_diagnostics.CurrentSource, failure.Span, failure.Detail));
                break;

            case ComptimeFailureKind.FuelExhausted:
                _diagnostics.Report(new ComptimeFuelExhaustedError(_diagnostics.CurrentSource, failure.Span));
                break;
        }
    }

    private BoundExpression BindIntrinsicCallExpression(IntrinsicCallExpressionSyntax intrinsic)
    {
        if (!P2InstructionMetadata.TryParseMnemonic(intrinsic.Name.Text, out P2Mnemonic mnemonic))
        {
            _diagnostics.Report(new UnknownBuiltinError(_diagnostics.CurrentSource, intrinsic.Name.Span, intrinsic.Name.Text));
            return new BoundErrorExpression(intrinsic.Span);
        }

        List<BoundExpression> arguments = new(intrinsic.Arguments.Count);
        foreach (ExpressionSyntax argument in intrinsic.Arguments)
            arguments.Add(BindExpression(argument));

        return new BoundIntrinsicCallExpression(mnemonic, arguments, intrinsic.Span, BuiltinTypes.U32);
    }

    private BoundExpression BindTypedStructLiteralExpression(TypedStructLiteralExpressionSyntax syntax)
    {
        // The parser guarantees TypeName is a NameExpressionSyntax.
        NameExpressionSyntax nameExpr = (NameExpressionSyntax)syntax.TypeName;
        BladeType resolvedType = ResolveTypeAlias(nameExpr.Name.Text, nameExpr.Name.Span);
        if (resolvedType is not StructTypeSymbol structType)
        {
            if (resolvedType is not UnknownTypeSymbol)
                _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, nameExpr.Name.Span, "struct", resolvedType.Name));
            return new BoundErrorExpression(syntax.Span);
        }

        // Bind each field initializer.
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<BoundStructFieldInitializer> initializers = new(syntax.Initializers.Count);
        foreach (FieldInitializerSyntax initializer in syntax.Initializers)
        {
            string fieldName = initializer.Name.Text;

            if (!seen.Add(fieldName))
            {
                _diagnostics.Report(new StructDuplicateFieldError(_diagnostics.CurrentSource, initializer.Name.Span, fieldName));
                BindExpression(initializer.Value);
                continue;
            }

            if (!structType.Fields.TryGetValue(fieldName, out BladeType? fieldType))
            {
                _diagnostics.Report(new StructUnknownFieldError(_diagnostics.CurrentSource, initializer.Name.Span, structType.Name, fieldName));
                BindExpression(initializer.Value);
                continue;
            }

            BoundExpression value = BindExpression(initializer.Value, fieldType);
            initializers.Add(new BoundStructFieldInitializer(fieldName, value));
        }

        // Check for missing fields.
        List<string>? missing = null;
        foreach (string fieldName in structType.Fields.Keys)
        {
            if (!seen.Contains(fieldName))
                (missing ??= new List<string>()).Add(fieldName);
        }

        if (missing is not null)
        {
            _diagnostics.Report(new StructMissingFieldsError(_diagnostics.CurrentSource, syntax.CloseBrace.Span, structType.Name, string.Join(", ", missing)));
        }

        return new BoundStructLiteralExpression(initializers, syntax.Span, structType);
    }

    private BoundExpression BindArrayLiteralExpression(ArrayLiteralExpressionSyntax arrayLiteral, BladeType? expectedType)
    {
        ArrayTypeSymbol? expectedArrayType = expectedType as ArrayTypeSymbol;
        int? expectedLength = expectedArrayType?.Length;
        BladeType? elementType = expectedArrayType?.ElementType;

        bool lastElementIsSpread = false;
        for (int i = 0; i < arrayLiteral.Elements.Count; i++)
        {
            ArrayElementSyntax element = arrayLiteral.Elements[i];
            if (element.Spread is null)
                continue;

            Token spreadToken = element.Spread.Value;
            if (i != arrayLiteral.Elements.Count - 1)
                _diagnostics.Report(new ArrayLiteralSpreadMustBeLastError(_diagnostics.CurrentSource, spreadToken.Span));
            else
                lastElementIsSpread = true;
        }

        if ((arrayLiteral.Elements.Count == 0 || lastElementIsSpread) && expectedLength is null)
        {
            _diagnostics.Report(new ArrayLiteralRequiresContextError(_diagnostics.CurrentSource, arrayLiteral.Span));
            foreach (ArrayElementSyntax element in arrayLiteral.Elements)
                _ = BindExpression(element.Value, elementType);
            return new BoundErrorExpression(arrayLiteral.Span);
        }

        List<BoundExpression> boundElements = new(arrayLiteral.Elements.Count);
        for (int i = 0; i < arrayLiteral.Elements.Count; i++)
        {
            ArrayElementSyntax element = arrayLiteral.Elements[i];
            BoundExpression boundElement;
            if (elementType is null)
            {
                boundElement = BindExpression(element.Value);
                elementType = boundElement.Type;
            }
            else
            {
                boundElement = BindExpression(element.Value, elementType);
            }

            boundElements.Add(boundElement);
        }

        int explicitCount = boundElements.Count;
        int producedLength = explicitCount;
        if (explicitCount == 0)
        {
            producedLength = expectedLength!.Value;
        }
        else if (lastElementIsSpread && expectedLength is int knownSpreadLength && knownSpreadLength > explicitCount)
        {
            producedLength = knownSpreadLength;
        }

        BladeType resolvedElementType = Requires.NotNull(elementType);
        ArrayTypeSymbol resultType = expectedArrayType is not null && expectedArrayType.Length is null
            ? new ArrayTypeSymbol(resolvedElementType)
            : new ArrayTypeSymbol(resolvedElementType, producedLength);
        return new BoundArrayLiteralExpression(boundElements, lastElementIsSpread, arrayLiteral.Span, resultType);
    }

    private BoundExpression BindIfExpression(IfExpressionSyntax ifExpression)
    {
        BoundExpression condition = BindExpression(ifExpression.Condition, BuiltinTypes.Bool);
        BoundExpression thenExpression = BindExpression(ifExpression.ThenExpression);
        BoundExpression elseExpression = BindExpression(ifExpression.ElseExpression);

        if (IsAssignable(thenExpression.Type, elseExpression.Type))
        {
            elseExpression = BindConversion(elseExpression, thenExpression.Type, elseExpression.Span, reportMismatch: false);
            return new BoundIfExpression(condition, thenExpression, elseExpression, ifExpression.Span, thenExpression.Type);
        }

        if (IsAssignable(elseExpression.Type, thenExpression.Type))
        {
            thenExpression = BindConversion(thenExpression, elseExpression.Type, thenExpression.Span, reportMismatch: false);
            return new BoundIfExpression(condition, thenExpression, elseExpression, ifExpression.Span, elseExpression.Type);
        }

        _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, ifExpression.Span, thenExpression.Type.Name, elseExpression.Type.Name));
        return new BoundIfExpression(condition, thenExpression, elseExpression, ifExpression.Span, BuiltinTypes.Unknown);
    }

    private BoundExpression BindRangeExpression(RangeExpressionSyntax rangeExpression)
    {
        _diagnostics.Report(new RangeExpressionOutsideForLoopError(_diagnostics.CurrentSource, rangeExpression.Span));
        _ = BindExpression(rangeExpression.Start);
        _ = BindExpression(rangeExpression.End);
        return new BoundErrorExpression(rangeExpression.Span);
    }

    private BoundRangeExpression BindLoopRangeExpression(RangeExpressionSyntax rangeExpression)
    {
        BoundExpression start = BindExpression(rangeExpression.Start);
        BoundExpression end = BindExpression(rangeExpression.End);

        ValidateRangeEndpoints(rangeExpression, start, end);

        return new BoundRangeExpression(start, end, rangeExpression.IsInclusive, rangeExpression.Span);
    }

    private void ValidateRangeEndpoints(RangeExpressionSyntax rangeExpression, BoundExpression start, BoundExpression end)
    {
        if (start.Type is not IntegerLiteralTypeSymbol and not IntegerTypeSymbol and not EnumTypeSymbol and not BitfieldTypeSymbol)
            _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, rangeExpression.Start.Span, "integer", start.Type.Name));
        if (end.Type is not IntegerLiteralTypeSymbol and not IntegerTypeSymbol and not EnumTypeSymbol and not BitfieldTypeSymbol)
            _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, rangeExpression.End.Span, "integer", end.Type.Name));
    }

    private BoundExpression BindCastExpression(CastExpressionSyntax castExpression)
    {
        BoundExpression expression = BindExpression(castExpression.Expression);
        BladeType targetType = BindType(castExpression.TargetType);

        if (!CanExplicitlyCast(expression.Type, targetType))
        {
            _diagnostics.Report(new InvalidExplicitCastError(_diagnostics.CurrentSource, castExpression.Span, expression.Type.Name, targetType.Name));
            return new BoundErrorExpression(castExpression.Span);
        }

        ReportComptimeIntegerTruncationIfNeeded(expression, targetType, castExpression.Span);
        return new BoundCastExpression(expression, castExpression.Span, targetType);
    }

    private BoundExpression BindBitcastExpression(BitcastExpressionSyntax bitcastExpression)
    {
        BoundExpression expression = BindExpression(bitcastExpression.Value);
        BladeType targetType = BindType(bitcastExpression.TargetType);

        int? sourceWidth = expression.Type switch
        {
            RuntimeTypeSymbol { IsScalarCastType: true, ScalarWidthBits: int width } => width,
            IntegerLiteralTypeSymbol when targetType is RuntimeTypeSymbol { IsScalarCastType: true, ScalarWidthBits: int inferredTargetWidth } => inferredTargetWidth,
            _ => default(int?),
        };

        if (sourceWidth is not int knownSourceWidth
            || targetType is not RuntimeTypeSymbol { IsScalarCastType: true, ScalarWidthBits: int targetWidth })
        {
            _diagnostics.Report(new InvalidExplicitCastError(_diagnostics.CurrentSource, bitcastExpression.Span, expression.Type.Name, targetType.Name));
            return new BoundErrorExpression(bitcastExpression.Span);
        }

        if (knownSourceWidth != targetWidth)
        {
            _diagnostics.Report(new BitcastSizeMismatchError(_diagnostics.CurrentSource, bitcastExpression.Span, expression.Type.Name, targetType.Name));
            return new BoundErrorExpression(bitcastExpression.Span);
        }

        return new BoundBitcastExpression(expression, bitcastExpression.Span, targetType);
    }

    private BoundExpression BindQueryExpression(QueryExpressionSyntax query)
    {
        string operatorName = SyntaxFacts.GetText(query.Keyword.Kind)!;

        if (query.IsTwoArgumentForm)
            return BindTwoArgQueryExpression(query, operatorName);

        return BindOneArgQueryExpression(query, operatorName);
    }

    private BoundExpression BindTwoArgQueryExpression(QueryExpressionSyntax query, string operatorName)
    {
        if (query.Keyword.Kind == TokenKind.MemoryofKeyword)
        {
            _diagnostics.Report(new MemoryofRequiresVariableError(_diagnostics.CurrentSource, query.Span));
            return new BoundErrorExpression(query.Span);
        }

        BladeType type = BindType(query.Subject);
        BoundExpression memorySpaceExpr = BindExpression(query.MemorySpace!, expectedType: MemorySpaceType);

        VariableStorageClass? storageClass = TryResolveMemorySpace(memorySpaceExpr, query.MemorySpace!.Span);
        if (storageClass is null)
            return new BoundErrorExpression(query.Span);

        if (query.Keyword.Kind == TokenKind.SizeofKeyword)
        {
            if (type is not RuntimeTypeSymbol runtimeType)
            {
                _diagnostics.Report(new QueryUnsupportedTypeError(_diagnostics.CurrentSource, query.Subject.Span, operatorName, type.Name));
                return new BoundErrorExpression(query.Span);
            }

            int size = runtimeType.GetSizeInMemorySpace(storageClass.Value);
            return new BoundLiteralExpression(new ComptimeBladeValue((ComptimeTypeSymbol)BuiltinTypes.IntegerLiteral, (long)size), query.Span);
        }

        Assert.Invariant(query.Keyword.Kind == TokenKind.AlignofKeyword, "Two-arg query must be sizeof or alignof.");

        if (type is not RuntimeTypeSymbol runtimeTypeForAlignment)
        {
            _diagnostics.Report(new QueryUnsupportedTypeError(_diagnostics.CurrentSource, query.Subject.Span, operatorName, type.Name));
            return new BoundErrorExpression(query.Span);
        }

        int alignment = runtimeTypeForAlignment.GetAlignmentInMemorySpace(storageClass.Value);
        return new BoundLiteralExpression(new ComptimeBladeValue((ComptimeTypeSymbol)BuiltinTypes.IntegerLiteral, (long)alignment), query.Span);
    }

    private BoundExpression BindOneArgQueryExpression(QueryExpressionSyntax query, string operatorName)
    {
        // The subject was parsed as TypeSyntax. For single-arg, it must be a variable.
        // NamedTypeSyntax covers identifiers, PrimitiveTypeSyntax covers type keywords like u32.
        if (query.Subject is not NamedTypeSyntax namedType)
        {
            // Subject is a primitive type keyword or complex type — not a variable name.
            if (query.Keyword.Kind == TokenKind.MemoryofKeyword)
            {
                _diagnostics.Report(new MemoryofRequiresVariableError(_diagnostics.CurrentSource, query.Subject.Span));
                return new BoundErrorExpression(query.Span);
            }

            _diagnostics.Report(new QueryRequiresMemorySpaceError(_diagnostics.CurrentSource, query.Subject.Span, operatorName));
            return new BoundErrorExpression(query.Span);
        }

        string name = namedType.Name.Text;

        if (!TryResolveAccessibleName(name, namedType.Name.Span, out Symbol? symbol) || symbol is null)
        {
            _diagnostics.Report(new UndefinedNameError(_diagnostics.CurrentSource, namedType.Name.Span, name));
            return new BoundErrorExpression(query.Span);
        }

        if (symbol is AutomaticVariableSymbol)
        {
            _diagnostics.Report(new QueryAutomaticLocalError(_diagnostics.CurrentSource, query.Subject.Span, operatorName, name));
            return new BoundErrorExpression(query.Span);
        }

        if (symbol is GlobalVariableSymbol globalVariable)
        {
            return FoldVariableQuery(query, globalVariable, operatorName);
        }

        // Symbol is not a variable (could be a function, module, or type alias).
        if (query.Keyword.Kind == TokenKind.MemoryofKeyword)
        {
            _diagnostics.Report(new MemoryofRequiresVariableError(_diagnostics.CurrentSource, query.Subject.Span));
            return new BoundErrorExpression(query.Span);
        }

        _diagnostics.Report(new QueryRequiresMemorySpaceError(_diagnostics.CurrentSource, query.Subject.Span, operatorName));
        return new BoundErrorExpression(query.Span);
    }

    private BoundExpression FoldVariableQuery(QueryExpressionSyntax query, GlobalVariableSymbol variable, string operatorName)
    {
        VariableStorageClass sc = variable.StorageClass;

        if (query.Keyword.Kind == TokenKind.MemoryofKeyword)
        {
            (string memberName, long value) = sc switch
            {
                VariableStorageClass.Cog => ("cog", 0L),
                VariableStorageClass.Lut => ("lut", 1L),
                VariableStorageClass.Hub => ("hub", 2L),
                _ => Assert.UnreachableValue<(string, long)>(), // pragma: force-coverage
            };

            return new BoundEnumLiteralExpression(MemorySpaceType, memberName, value, query.Span);
        }

        if (query.Keyword.Kind == TokenKind.SizeofKeyword)
        {
            if (variable.Type is not RuntimeTypeSymbol runtimeVariableType)
            {
                _diagnostics.Report(new QueryUnsupportedTypeError(_diagnostics.CurrentSource, query.Subject.Span, operatorName, variable.Type.Name));
                return new BoundErrorExpression(query.Span);
            }

            int size = runtimeVariableType.GetSizeInMemorySpace(sc);
            return new BoundLiteralExpression(new ComptimeBladeValue((ComptimeTypeSymbol)BuiltinTypes.IntegerLiteral, (long)size), query.Span);
        }

        Assert.Invariant(query.Keyword.Kind == TokenKind.AlignofKeyword, "Variable query must be sizeof, alignof, or memoryof.");

        if (variable.Type is not RuntimeTypeSymbol runtimeVariableTypeForAlignment)
        {
            _diagnostics.Report(new QueryUnsupportedTypeError(_diagnostics.CurrentSource, query.Subject.Span, operatorName, variable.Type.Name));
            return new BoundErrorExpression(query.Span);
        }

        int alignment = runtimeVariableTypeForAlignment.GetAlignmentInMemorySpace(sc);
        return new BoundLiteralExpression(new ComptimeBladeValue((ComptimeTypeSymbol)BuiltinTypes.IntegerLiteral, (long)alignment), query.Span);
    }

    private VariableStorageClass? TryResolveMemorySpace(BoundExpression expression, TextSpan span)
    {
        if (expression is BoundEnumLiteralExpression enumLiteral)
        {
            return enumLiteral.Value switch
            {
                0 => VariableStorageClass.Cog,
                1 => VariableStorageClass.Lut,
                2 => VariableStorageClass.Hub,
                _ => ReportInvalidMemorySpace(span),
            };
        }

        int? value = TryEvaluateConstantInt(expression);
        if (value is not null)
        {
            return value switch
            {
                0 => VariableStorageClass.Cog,
                1 => VariableStorageClass.Lut,
                2 => VariableStorageClass.Hub,
                _ => ReportInvalidMemorySpace(span),
            };
        }

        _diagnostics.Report(new InvalidMemorySpaceArgumentError(_diagnostics.CurrentSource, span));
        return null;
    }

    private VariableStorageClass? ReportInvalidMemorySpace(TextSpan span)
    {
        _diagnostics.Report(new InvalidMemorySpaceArgumentError(_diagnostics.CurrentSource, span));
        return null;
    }

    private List<BoundExpression> BindCallArguments(
        FunctionSymbol function,
        SeparatedSyntaxList<ExpressionSyntax> arguments,
        TextSpan callSiteSpan)
    {
        bool hasNamedArguments = false;
        foreach (ExpressionSyntax argument in arguments)
        {
            if (argument is NamedArgumentSyntax)
            {
                hasNamedArguments = true;
                break;
            }
        }

        if (!hasNamedArguments)
        {
            if (arguments.Count != function.Parameters.Count)
            {
                _diagnostics.Report(new ArgumentCountMismatchError(_diagnostics.CurrentSource, callSiteSpan, function.Name, function.Parameters.Count, arguments.Count));
            }

            List<BoundExpression> positionalArguments = new(arguments.Count);
            int compared = Math.Min(arguments.Count, function.Parameters.Count);
            for (int i = 0; i < compared; i++)
                positionalArguments.Add(BindExpression(arguments[i], function.Parameters[i].Type));

            for (int i = compared; i < arguments.Count; i++)
                positionalArguments.Add(BindExpression(arguments[i]));

            return positionalArguments;
        }

        BoundExpression?[] reordered = new BoundExpression?[function.Parameters.Count];
        bool[] filled = new bool[function.Parameters.Count];
        bool[] filledByNamed = new bool[function.Parameters.Count];
        bool sawNamedArgument = false;
        int nextPositionalIndex = 0;
        int filledCount = 0;

        foreach (ExpressionSyntax argument in arguments)
        {
            if (argument is NamedArgumentSyntax namedArgument)
            {
                sawNamedArgument = true;
                int parameterIndex = FindParameterIndex(function, namedArgument.Name.Text);
                if (parameterIndex < 0)
                {
                    _diagnostics.Report(new UnknownNamedArgumentError(_diagnostics.CurrentSource, namedArgument.Name.Span, function.Name, namedArgument.Name.Text));
                    _ = BindExpression(namedArgument.Value);
                    continue;
                }

                BladeType parameterType = function.Parameters[parameterIndex].Type;
                BoundExpression boundValue = BindExpression(namedArgument.Value, parameterType);
                if (filled[parameterIndex])
                {
                    if (filledByNamed[parameterIndex])
                    {
                        _diagnostics.Report(new DuplicateNamedArgumentError(_diagnostics.CurrentSource, namedArgument.Name.Span, namedArgument.Name.Text));
                    }
                    else
                    {
                        _diagnostics.Report(new NamedArgumentConflictsWithPositionalError(_diagnostics.CurrentSource, namedArgument.Name.Span, namedArgument.Name.Text));
                    }

                    continue;
                }


                reordered[parameterIndex] = boundValue;
                filled[parameterIndex] = true;
                filledByNamed[parameterIndex] = true;
                filledCount++;
                continue;
            }

            if (sawNamedArgument)
            {
                _diagnostics.Report(new PositionalArgumentAfterNamedError(_diagnostics.CurrentSource, argument.Span, function.Name));
                _ = BindExpression(argument);
                continue;
            }

            if (nextPositionalIndex < function.Parameters.Count)
            {
                reordered[nextPositionalIndex] = BindExpression(argument, function.Parameters[nextPositionalIndex].Type);
                filled[nextPositionalIndex] = true;
                filledCount++;
            }
            else
            {
                _ = BindExpression(argument);
            }

            nextPositionalIndex++;
        }

        if (filledCount != function.Parameters.Count)
        {
            _diagnostics.Report(new ArgumentCountMismatchError(_diagnostics.CurrentSource, callSiteSpan, function.Name, function.Parameters.Count, filledCount));
        }

        List<BoundExpression> boundArguments = new(function.Parameters.Count);
        for (int i = 0; i < function.Parameters.Count; i++)
        {
            boundArguments.Add(reordered[i] ?? new BoundErrorExpression(callSiteSpan));
        }

        return boundArguments;
    }

    private static int FindParameterIndex(FunctionSymbol function, string parameterName)
    {
        for (int i = 0; i < function.Parameters.Count; i++)
        {
            if (string.Equals(function.Parameters[i].Name, parameterName, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private List<BoundExpression> BindArgumentsLoose(SeparatedSyntaxList<ExpressionSyntax> arguments)
    {
        List<BoundExpression> bound = new(arguments.Count);
        foreach (ExpressionSyntax argument in arguments)
            bound.Add(BindExpression(argument));
        return bound;
    }

    private static bool TryGetFunctionSymbol(BoundExpression expression, out FunctionSymbol? function)
    {
        if (expression is BoundSymbolExpression symbolExpression && symbolExpression.Symbol is FunctionSymbol directFunction)
        {
            function = directFunction;
            return true;
        }

        if (expression.Type is FunctionTypeSymbol functionType)
        {
            function = functionType.Function;
            return true;
        }

        function = null;
        return false;
    }

    private BoundExpression BindConversion(BoundExpression expression, BladeType targetType, TextSpan span, bool reportMismatch)
    {
        if (targetType is UnknownTypeSymbol || expression.Type is UnknownTypeSymbol)
            return expression;

        if (targetType == expression.Type)
            return expression;

        if (TryGetLiteralU8Bytes(expression, out byte[] literalBytes)
            && targetType is MultiPointerTypeSymbol targetPointer
            && targetPointer.PointeeType == BuiltinTypes.U8)
        {
            if (!targetPointer.IsConst)
            {
                _diagnostics.Report(new StringToNonConstPointerError(_diagnostics.CurrentSource, span));
                return new BoundErrorExpression(span);
            }

            return LowerByteArrayToPointerLiteral(literalBytes, targetPointer, span);
        }

        if (!IsAssignable(targetType, expression.Type))
        {
            if (reportMismatch)
                _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, span, targetType.Name, expression.Type.Name));
            return new BoundErrorExpression(span);
        }

        ReportComptimeIntegerTruncationIfNeeded(expression, targetType, span);
        return new BoundConversionExpression(expression, span, targetType);
    }

    private void ReportComptimeIntegerTruncationIfNeeded(BoundExpression expression, BladeType targetType, TextSpan span)
    {
        if (!TryGetCompileTimeIntegerWidth(expression.Type, out int sourceWidth)
            || !TryGetCompileTimeIntegerWidth(targetType, out int targetWidth)
            || targetWidth < 8
            || targetWidth >= sourceWidth
            || !TryEvaluateConstantValue(expression, out ComptimeResult value)
            || !TryConvertConstantToInt64(value, out long exactValue))
        {
            return;
        }

        ulong mask = (1UL << targetWidth) - 1UL;
        ulong lowBits = unchecked((ulong)exactValue) & mask;
        long zeroExtended = unchecked((long)lowBits);
        long signExtended = SignExtend(lowBits, targetWidth);
        if (exactValue == zeroExtended || exactValue == signExtended)
            return;

        if (BladeValue.TryConvert(BladeValue.IntegerLiteral(exactValue), targetType, out BladeValue truncatedValue) != EvaluationError.None)
            return;

        _diagnostics.Report(new ComptimeIntegerTruncationWarning(_diagnostics.CurrentSource, span, exactValue.ToString(CultureInfo.InvariantCulture), targetType.Name, truncatedValue.Format()));
    }

    private static bool TryGetCompileTimeIntegerWidth(BladeType type, out int width)
    {
        if (type == BuiltinTypes.IntegerLiteral)
        {
            width = 64;
            return true;
        }

        if (type is RuntimeTypeSymbol { ScalarWidthBits: int runtimeWidth })
        {
            width = runtimeWidth;
            return true;
        }

        width = 0;
        return false;
    }

    private static long SignExtend(ulong value, int width)
    {
        if (width >= 64)
            return unchecked((long)value);

        int shift = 64 - width;
        return unchecked(((long)value << shift) >> shift);
    }

    private static BoundExpression LowerByteArrayToPointerLiteral(byte[] bytes, MultiPointerTypeSymbol targetPointer, TextSpan span)
    {
        RuntimeBladeValue literalValue = BladeValue.U8Array(bytes);
        GlobalVariableSymbol literalSymbol = new(
            FormattableString.Invariant($"lit_{targetPointer.StorageClass}_{span.Start}_{span.Length}"),
            literalValue.Type,
            isConst: true,
            targetPointer.StorageClass,
            declaringLayout: null,
            isExtern: false,
            fixedAddress: null,
            alignment: null,
            sourceSpan: SourceSpan.Synthetic());
        literalSymbol.SetInitializer(new BoundLiteralExpression(literalValue, span));
        return new BoundLiteralExpression(
            BladeValue.Pointer(targetPointer, new PointedValue(literalSymbol, 0)),
            span);
    }

    private static bool CanExplicitlyCast(BladeType sourceType, BladeType targetType)
    {
        if (sourceType is UnknownTypeSymbol || targetType is UnknownTypeSymbol)
            return true;

        if (sourceType == targetType)
            return true;

        if (sourceType is EnumTypeSymbol sourceEnum && targetType is EnumTypeSymbol targetEnum)
            return sourceEnum == targetEnum;

        if (sourceType is EnumTypeSymbol openEnumSource
            && openEnumSource.IsOpen
            && targetType is IntegerLiteralTypeSymbol or IntegerTypeSymbol or EnumTypeSymbol or BitfieldTypeSymbol
            && openEnumSource.BackingType == targetType)
        {
            return true;
        }

        if (targetType is EnumTypeSymbol openEnumTarget
            && openEnumTarget.IsOpen
            && sourceType is IntegerLiteralTypeSymbol or IntegerTypeSymbol or EnumTypeSymbol or BitfieldTypeSymbol
            && openEnumTarget.BackingType == sourceType)
        {
            return true;
        }

        if (sourceType is IntegerLiteralTypeSymbol or IntegerTypeSymbol or BitfieldTypeSymbol
            && targetType is RuntimeTypeSymbol { ScalarWidthBits: not null }
            && targetType is IntegerTypeSymbol or EnumTypeSymbol or BitfieldTypeSymbol)
        {
            return true;
        }

        return sourceType is PointerLikeTypeSymbol && targetType is PointerLikeTypeSymbol;
    }

    private LocalVariableSymbol CreateLocalVariableSymbol(
        VariableDeclarationSyntax declaration,
        BladeType variableType)
    {
        bool isConst = declaration.MutabilityKeyword.Kind == TokenKind.ConstKeyword;
        if (declaration.ExternKeyword is Token externKeyword)
            _diagnostics.Report(new InvalidExternScopeError(_diagnostics.CurrentSource, externKeyword.Span));

        if (declaration.StorageClassKeyword is Token storageClassKeyword)
            _diagnostics.Report(new InvalidLocalStorageClassError(_diagnostics.CurrentSource, storageClassKeyword.Span, storageClassKeyword.Text));

        return new LocalVariableSymbol(
            declaration.Name.Text,
            variableType,
            isConst,
            sourceSpan: CreateSourceSpan(declaration.Name.Span));
    }

    private GlobalVariableSymbol CreateGlobalVariableSymbol(
        VariableDeclarationSyntax declaration,
        BladeType variableType,
        LayoutSymbol? declaringLayout)
    {
        VariableStorageClass? storageClass = MapStorageClass(declaration.StorageClassKeyword);
        Assert.Invariant(storageClass.HasValue, "Global variable declarations must have an explicit storage class.");
        return new GlobalVariableSymbol(
            declaration.Name.Text,
            variableType,
            declaration.MutabilityKeyword.Kind == TokenKind.ConstKeyword,
            storageClass.Value,
            declaringLayout,
            declaration.ExternKeyword is not null,
            fixedAddress: null,
            alignment: null,
            sourceSpan: CreateSourceSpan(declaration.Name.Span));
    }

    private static VariableStorageClass? MapStorageClass(Token? storageClassKeyword)
    {
        return storageClassKeyword?.Kind switch
        {
            TokenKind.CogKeyword => VariableStorageClass.Cog,
            TokenKind.LutKeyword => VariableStorageClass.Lut,
            TokenKind.HubKeyword => VariableStorageClass.Hub,
            _ => null,
        };
    }

    private bool IsSupportedPlainTopLevelGlobal(VariableDeclarationSyntax declaration, bool reportDiagnostic)
    {
        VariableStorageClass? storageClass = MapStorageClass(declaration.StorageClassKeyword);
        Assert.Invariant(storageClass.HasValue, "Stored top-level declarations must have an explicit storage class.");

        if (storageClass == VariableStorageClass.Hub)
            return true;

        if (reportDiagnostic)
        {
            Assert.Invariant(declaration.StorageClassKeyword is not null, "Stored declarations must carry a storage-class token.");
            Token storageClassKeyword = declaration.StorageClassKeyword!.Value;
            _diagnostics.Report(new UnsupportedGlobalStorageError(_diagnostics.CurrentSource, storageClassKeyword.Span, storageClassKeyword.Text));
        }

        return false;
    }

    private VariableStorageClass BindPointerStorageClass(Token? storageClassKeyword, TextSpan span)
    {
        VariableStorageClass? storageClass = MapStorageClass(storageClassKeyword);
        if (storageClass is VariableStorageClass explicitStorageClass)
            return explicitStorageClass;

        if (!_suppressPointerStorageClassDiagnostics)
            _diagnostics.Report(new PointerStorageClassRequiredError(_diagnostics.CurrentSource, span));
        return VariableStorageClass.Cog;
    }

    private void ResolveLayoutMetadata(VariableDeclarationSyntax declaration, GlobalVariableSymbol variableSymbol)
    {
        int? fixedAddress = declaration.AtClause is null
            ? null
            : BindRequiredConstantInt(declaration.AtClause.Address, declaration.AtClause.Address.Span);
        int? alignment = declaration.AlignClause is null
            ? null
            : BindRequiredConstantInt(declaration.AlignClause.Alignment, declaration.AlignClause.Alignment.Span);
        variableSymbol.SetLayoutMetadata(fixedAddress, alignment);
    }

    private int? BindRequiredConstantInt(ExpressionSyntax expression, TextSpan span)
    {
        BoundExpression bound = BindExpression(expression);
        bound = RequireComptimeExpression(bound, span);
        int? value = TryEvaluateConstantInt(bound);
        if (value is null && bound is not BoundErrorExpression)
            _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, span, "comptime integer", bound.Type.Name));
        return value;
    }

    private int? TryEvaluateConstantInt(ExpressionSyntax? expression)
    {
        if (expression is null)
            return null;

        BoundExpression bound = BindExpression(expression);
        return TryEvaluateConstantInt(bound);
    }

    private bool TryEvaluateConstantValue(BoundExpression expression, out ComptimeResult value)
    {
        return TryEvaluateFoldedValue(expression, out value, out _);
    }

    private int? TryEvaluateConstantInt(BoundExpression expression)
    {
        if (!TryEvaluateConstantValue(expression, out ComptimeResult value))
            return null;

        if (!TryConvertConstantToInt64(value, out long constantValue))
            return null;

        return constantValue is >= int.MinValue and <= int.MaxValue
            ? (int)constantValue
            : null;
    }

    private static bool TryConvertConstantToInt64(ComptimeResult value, out long converted)
    {
        return value.TryConvertToLong(out converted);
    }

    private BladeType BindType(TypeSyntax syntax, string? aliasName = null)
    {
        return syntax switch
        {
            PrimitiveTypeSyntax primitive => BindPrimitiveType(primitive.Keyword),
            GenericWidthTypeSyntax generic => BindGenericWidthType(generic),
            ArrayTypeSyntax array => BindArrayType(array),
            PointerTypeSyntax pointer => new PointerTypeSymbol(
                BindType(pointer.PointeeType),
                pointer.ConstKeyword is not null,
                BindPointerStorageClass(pointer.StorageClassKeyword, pointer.Span),
                pointer.VolatileKeyword is not null,
                TryEvaluateConstantInt(pointer.AlignClause?.Alignment)),
            MultiPointerTypeSyntax multiPointer => new MultiPointerTypeSymbol(
                BindType(multiPointer.PointeeType),
                multiPointer.ConstKeyword is not null,
                BindPointerStorageClass(multiPointer.StorageClassKeyword, multiPointer.Span),
                multiPointer.VolatileKeyword is not null,
                TryEvaluateConstantInt(multiPointer.AlignClause?.Alignment)),
            StructTypeSyntax structType => BindStructType(structType, aliasName),
            UnionTypeSyntax unionType => BindUnionType(unionType, aliasName),
            EnumTypeSyntax enumType => BindEnumType(enumType, aliasName),
            BitfieldTypeSyntax bitfieldType => BindBitfieldType(bitfieldType, aliasName),
            NamedTypeSyntax named => BindNamedType(named),
            QualifiedTypeSyntax qualified => BindQualifiedType(qualified),
            _ => Assert.UnreachableValue<BladeType>("all type syntax nodes are handled above") // pragma: force-coverage
        };
    }

    private BladeType BindPrimitiveType(Token keywordToken)
    {
        if (BuiltinTypes.TryGet(keywordToken.Text, out BladeType type))
            return type;

        _diagnostics.Report(new UndefinedTypeError(_diagnostics.CurrentSource, keywordToken.Span, keywordToken.Text));
        return BuiltinTypes.Unknown;
    }

    private BladeType BindGenericWidthType(GenericWidthTypeSyntax genericType)
    {
        BoundExpression width = BindExpression(genericType.Width);
        if (width.Type is not IntegerLiteralTypeSymbol and not IntegerTypeSymbol and not EnumTypeSymbol and not BitfieldTypeSymbol)
            _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, genericType.Width.Span, "integer", width.Type.Name));

        return genericType.Keyword.Kind == TokenKind.UintKeyword ? BuiltinTypes.Uint : BuiltinTypes.Int;
    }

    private BladeType BindArrayType(ArrayTypeSyntax arrayType)
    {
        BoundExpression size = BindExpression(arrayType.Size);
        if (size.Type is not IntegerLiteralTypeSymbol and not IntegerTypeSymbol and not EnumTypeSymbol and not BitfieldTypeSymbol)
            _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, arrayType.Size.Span, "integer", size.Type.Name));

        BladeType elementType = BindType(arrayType.ElementType);
        return new ArrayTypeSymbol(elementType, TryEvaluateConstantInt(size));
    }

    private BladeType BindStructType(StructTypeSyntax structType, string? aliasName)
    {
        Dictionary<string, BladeType> fields = new(StringComparer.Ordinal);
        Dictionary<string, AggregateMemberSymbol> members = new(StringComparer.Ordinal);
        int nextOffset = 0;
        int maxAlignment = 1;
        foreach (StructFieldSyntax field in structType.Fields)
        {
            BladeType fieldType = BindType(field.Type);
            if (!fields.TryAdd(field.Name.Text, fieldType))
            {
                _diagnostics.Report(new SymbolAlreadyDeclaredError(_diagnostics.CurrentSource, field.Name.Span, field.Name.Text));
                continue;
            }

            int fieldSize = fieldType is RuntimeTypeSymbol runtimeFieldType ? runtimeFieldType.SizeBytes : 0;
            int fieldAlignment = fieldType is RuntimeTypeSymbol runtimeAlignedFieldType ? runtimeAlignedFieldType.AlignmentBytes : 1;
            nextOffset = AlignTo(nextOffset, fieldAlignment);

            members[field.Name.Text] = new AggregateMemberSymbol(field.Name.Text, fieldType, nextOffset, bitOffset: 0, bitWidth: 0, isBitfield: false);
            nextOffset += fieldSize;
            maxAlignment = Math.Max(maxAlignment, fieldAlignment);
        }

        string name = aliasName ?? $"<anon-struct#{++_anonymousStructIndex}>";
        int sizeBytes = AlignTo(nextOffset, maxAlignment);
        return new StructTypeSymbol(name, fields, members, sizeBytes, maxAlignment);
    }

    private BladeType BindUnionType(UnionTypeSyntax unionType, string? aliasName)
    {
        Dictionary<string, BladeType> fields = new(StringComparer.Ordinal);
        Dictionary<string, AggregateMemberSymbol> members = new(StringComparer.Ordinal);
        int maxSize = 0;
        int maxAlignment = 1;
        foreach (StructFieldSyntax field in unionType.Fields)
        {
            BladeType fieldType = BindType(field.Type);
            if (!fields.TryAdd(field.Name.Text, fieldType))
            {
                _diagnostics.Report(new SymbolAlreadyDeclaredError(_diagnostics.CurrentSource, field.Name.Span, field.Name.Text));
                continue;
            }

            int fieldSize = fieldType is RuntimeTypeSymbol runtimeFieldType ? runtimeFieldType.SizeBytes : 0;
            int fieldAlignment = fieldType is RuntimeTypeSymbol runtimeAlignedFieldType ? runtimeAlignedFieldType.AlignmentBytes : 1;
            members[field.Name.Text] = new AggregateMemberSymbol(field.Name.Text, fieldType, byteOffset: 0, bitOffset: 0, bitWidth: 0, isBitfield: false);
            maxSize = Math.Max(maxSize, fieldSize);
            maxAlignment = Math.Max(maxAlignment, fieldAlignment);
        }

        string name = aliasName ?? $"<anon-union#{++_anonymousStructIndex}>";
        return new UnionTypeSymbol(name, fields, members, maxSize, maxAlignment);
    }

    private BladeType BindEnumType(EnumTypeSyntax enumTypeSyntax, string? aliasName)
    {
        BladeType backingType = BindType(enumTypeSyntax.BackingType);
        if (backingType is not IntegerLiteralTypeSymbol and not IntegerTypeSymbol and not EnumTypeSymbol and not BitfieldTypeSymbol)
            _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, enumTypeSyntax.BackingType.Span, "integer", backingType.Name));

        Dictionary<string, long> members = new(StringComparer.Ordinal);
        long nextValue = 0;
        bool hasPreviousValue = false;
        bool isOpen = false;

        foreach (EnumMemberSyntax member in enumTypeSyntax.Members)
        {
            if (member.IsOpenMarker)
            {
                isOpen = true;
                continue;
            }

            if (members.ContainsKey(member.Name.Text))
            {
                _diagnostics.Report(new SymbolAlreadyDeclaredError(_diagnostics.CurrentSource, member.Name.Span, member.Name.Text));
                continue;
            }

            long value;
            if (member.Value is not null)
            {
                BoundExpression boundValue = BindExpression(member.Value);
                boundValue = RequireComptimeExpression(boundValue, member.Value.Span);
                int? constantValue = TryEvaluateConstantInt(boundValue);
                if (constantValue is null)
                {
                    _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, member.Value.Span, "comptime integer", boundValue.Type.Name));
                    value = nextValue;
                }
                else
                {
                    value = constantValue.Value;
                    nextValue = value + 1;
                    hasPreviousValue = true;
                }
            }
            else
            {
                value = hasPreviousValue ? nextValue : 0;
                nextValue = value + 1;
                hasPreviousValue = true;
            }

            members[member.Name.Text] = value;
        }

        string name = aliasName ?? $"<anon-enum#{++_anonymousStructIndex}>";
        return new EnumTypeSymbol(name, (RuntimeTypeSymbol)backingType, members, isOpen);
    }

    private BladeType BindBitfieldType(BitfieldTypeSyntax bitfieldTypeSyntax, string? aliasName)
    {
        BladeType backingType = BindType(bitfieldTypeSyntax.BackingType);
        if (backingType is not IntegerLiteralTypeSymbol and not IntegerTypeSymbol and not EnumTypeSymbol and not BitfieldTypeSymbol)
            _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, bitfieldTypeSyntax.BackingType.Span, "integer", backingType.Name));

        Dictionary<string, BladeType> fields = new(StringComparer.Ordinal);
        Dictionary<string, AggregateMemberSymbol> members = new(StringComparer.Ordinal);
        int bitOffset = 0;
        int backingWidth = backingType is RuntimeTypeSymbol { ScalarWidthBits: int width } ? width : 0;

        foreach (StructFieldSyntax field in bitfieldTypeSyntax.Fields)
        {
            BladeType fieldType = BindType(field.Type);
            if (!fields.TryAdd(field.Name.Text, fieldType))
            {
                _diagnostics.Report(new SymbolAlreadyDeclaredError(_diagnostics.CurrentSource, field.Name.Span, field.Name.Text));
                continue;
            }

            if (fieldType is not RuntimeTypeSymbol { BitfieldFieldWidthBits: int fieldWidth })
            {
                _diagnostics.Report(new TypeMismatchError(_diagnostics.CurrentSource, field.Type.Span, "bitfield scalar", fieldType.Name));
                fieldWidth = 0;
            }

            if (bitOffset + fieldWidth > backingWidth)
                _diagnostics.Report(new BitfieldWidthOverflowError(_diagnostics.CurrentSource, field.Span, aliasName ?? "<anon-bitfield>", field.Name.Text, bitOffset + fieldWidth, backingWidth));

            members[field.Name.Text] = new AggregateMemberSymbol(field.Name.Text, fieldType, byteOffset: 0, bitOffset, fieldWidth, isBitfield: true);
            bitOffset += fieldWidth;
        }

        string name = aliasName ?? $"<anon-bitfield#{++_anonymousStructIndex}>";
        return new BitfieldTypeSymbol(name, (RuntimeTypeSymbol)backingType, fields, members);
    }

    private BladeType BindNamedType(NamedTypeSyntax namedType)
    {
        bool isBuiltinTypeName = BuiltinTypes.TryGet(namedType.Name.Text, out _);
        Assert.Invariant(!isBuiltinTypeName, "Builtin type keywords should bind as PrimitiveTypeSyntax, not NamedTypeSyntax.");

        Symbol? resolvedSymbol = TryGetAccessibleLexicalSymbol(namedType.Name.Text);
        if (resolvedSymbol is TypeSymbol typeSymbol)
            return ResolveTypeAlias(typeSymbol, namedType.Name.Span);

        return ResolveTypeAlias(namedType.Name.Text, namedType.Name.Span);
    }

    private BladeType BindQualifiedType(QualifiedTypeSyntax qualifiedType)
    {
        string qualifiedName = string.Join('.', qualifiedType.Parts.Select(static part => part.Text));
        Token root = qualifiedType.Parts[0];
        if (!_globalScope.TryLookup(root.Text, out Symbol? symbol) || symbol is not ModuleSymbol moduleSymbol)
        {
            _diagnostics.Report(new UndefinedTypeError(_diagnostics.CurrentSource, qualifiedType.Span, qualifiedName));
            return BuiltinTypes.Unknown;
        }

        BoundModule module = moduleSymbol.Module;
        for (int i = 1; i < qualifiedType.Parts.Count - 1; i++)
        {
            Token segment = qualifiedType.Parts[i];
            if (!module.ExportedSymbols.TryGetValue(segment.Text, out Symbol? nestedSymbol)
                || nestedSymbol is not ModuleSymbol nestedModule)
            {
                _diagnostics.Report(new UndefinedTypeError(_diagnostics.CurrentSource, segment.Span, qualifiedName));
                return BuiltinTypes.Unknown;
            }

            module = nestedModule.Module;
        }

        Token finalSegment = qualifiedType.Parts[^1];
        if (!module.ExportedSymbols.TryGetValue(finalSegment.Text, out Symbol? resolvedSymbol)
            || resolvedSymbol is not TypeSymbol resolvedType)
        {
            _diagnostics.Report(new UndefinedTypeError(_diagnostics.CurrentSource, finalSegment.Span, qualifiedName));
            return BuiltinTypes.Unknown;
        }

        if (!resolvedType.IsResolved)
            _ = ResolveTypeAlias(resolvedType, finalSegment.Span);

        return resolvedType.Type;
    }

    private BladeType ResolveTypeAlias(TypeSymbol alias, TextSpan span)
    {
        Requires.NotNull(alias);

        if (alias.IsResolved)
            return alias.Type;

        if (!_typeAliasResolutionStack.Add(alias.Name))
        {
            _diagnostics.Report(new UndefinedTypeError(_diagnostics.CurrentSource, span, alias.Name));
            return BuiltinTypes.Unknown;
        }

        Assert.Invariant(_typeAliasDeclarations.TryGetValue(alias, out TypeAliasDeclarationSyntax? aliasSyntax), $"Type alias '{alias.Name}' must retain its declaration syntax until resolved.");
        BladeType boundType = BindType(Requires.NotNull(aliasSyntax).Type, aliasSyntax.Name.Text);
        _typeAliasResolutionStack.Remove(alias.Name);
        alias.Resolve(boundType);
        return alias.Type;
    }

    private BladeType ResolveTypeAlias(string aliasName, TextSpan span)
    {
        if (!_typeAliases.TryGetValue(aliasName, out TypeSymbol? alias))
        {
            _diagnostics.Report(new UndefinedTypeError(_diagnostics.CurrentSource, span, aliasName));
            return BuiltinTypes.Unknown;
        }

        return ResolveTypeAlias(alias, span);
    }

    private static BladeType BestNumericType(BladeType left, BladeType right)
    {
        if (left is UnknownTypeSymbol || right is UnknownTypeSymbol)
            return BuiltinTypes.Unknown;
        if (left == BuiltinTypes.IntegerLiteral && right == BuiltinTypes.IntegerLiteral)
            return BuiltinTypes.IntegerLiteral;
        if (left == BuiltinTypes.IntegerLiteral)
            return right;
        return left;
    }

    private static bool IsComparable(BladeType left, BladeType right)
    {
        if (left is UnknownTypeSymbol || right is UnknownTypeSymbol)
            return true;
        if (left is UndefinedLiteralTypeSymbol || right is UndefinedLiteralTypeSymbol)
            return true;
        if (left is IntegerLiteralTypeSymbol or IntegerTypeSymbol or EnumTypeSymbol or BitfieldTypeSymbol
            && right is IntegerLiteralTypeSymbol or IntegerTypeSymbol or EnumTypeSymbol or BitfieldTypeSymbol)
        {
            return true;
        }

        if (left is BoolTypeSymbol && right is BoolTypeSymbol)
            return true;
        return left == right;
    }

    private static bool IsAssignable(BladeType target, BladeType source)
    {
        if (target is UnknownTypeSymbol || source is UnknownTypeSymbol)
            return true;
        if (source is UndefinedLiteralTypeSymbol)
            return true;

        if (target is EnumTypeSymbol targetEnum && source is EnumTypeSymbol sourceEnum)
            return targetEnum == sourceEnum;

        if (target is BitfieldTypeSymbol targetBitfield && source is BitfieldTypeSymbol sourceBitfield)
            return targetBitfield == sourceBitfield;

        if (target is IntegerLiteralTypeSymbol or IntegerTypeSymbol or BitfieldTypeSymbol
            && source is IntegerLiteralTypeSymbol or IntegerTypeSymbol or BitfieldTypeSymbol)
        {
            return true;
        }

        if (target is BoolTypeSymbol && source is BoolTypeSymbol)
            return true;

        if (target is PointerLikeTypeSymbol targetPointer && source is PointerLikeTypeSymbol sourcePointer)
            return IsPointerAssignable(targetPointer, sourcePointer);

        if (target is StructTypeSymbol or UnionTypeSymbol)
            return target == source;

        return target == source;
    }

    private static bool AreCompatiblePointerSubtractionOperands(MultiPointerTypeSymbol left, MultiPointerTypeSymbol right)
    {
        return left.StorageClass == right.StorageClass
            && left.PointeeType == right.PointeeType;
    }

    private static int AlignTo(int value, int alignment)
    {
        if (alignment <= 1)
            return value;

        int remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    private static bool IsPointerAssignable(PointerLikeTypeSymbol target, PointerLikeTypeSymbol source)
    {
        if (target.GetType() != source.GetType())
            return false;

        if (target.StorageClass != source.StorageClass)
            return false;

        if (!target.IsConst && source.IsConst)
            return false;

        if (!target.IsVolatile && source.IsVolatile)
            return false;

        if (target.Alignment is int requiredAlignment)
        {
            if (source.Alignment is not int sourceAlignment || sourceAlignment < requiredAlignment)
                return false;
        }

        return IsAssignable(target.PointeeType, source.PointeeType);
    }

    private static bool TryGetLiteralU8Bytes(BoundExpression expression, out byte[] bytes)
    {
        if (expression is BoundLiteralExpression literal
            && literal.Value.TryGetU8Array(out bytes))
        {
            return true;
        }

        bytes = [];
        return false;
    }

    private static bool TryDecodeUtf8Literal(Token token, out string text)
    {
        if (token.Value is BladeValue value
            && value.TryGetU8Array(out byte[] bytes))
        {
            text = System.Text.Encoding.UTF8.GetString(bytes);
            return true;
        }

        text = string.Empty;
        return false;
    }

    private static string DecodeUtf8Literal(Token token)
    {
        Assert.Invariant(
            TryDecodeUtf8Literal(token, out string text),
            $"Token '{token.Kind}' must carry a UTF-8 byte-array literal value.");
        return text;
    }

    private void PushLoop(LoopContext kind) => _loopStack.Push(kind);

    private void PopLoop()
    {
        if (_loopStack.Count > 0)
            _loopStack.Pop();
    }

    private static (FunctionKind Kind, FunctionInliningPolicy InliningPolicy) GetFunctionModifiers(IReadOnlyList<Token> modifiers)
    {
        FunctionKind kind = FunctionKind.Default;
        FunctionInliningPolicy inliningPolicy = FunctionInliningPolicy.Default;

        foreach (Token modifier in modifiers)
        {
            switch (modifier.Kind)
            {
                case TokenKind.LeafKeyword:
                    kind = FunctionKind.Leaf;
                    break;
                case TokenKind.RecKeyword:
                    kind = FunctionKind.Rec;
                    break;
                case TokenKind.CoroKeyword:
                    kind = FunctionKind.Coro;
                    break;
                case TokenKind.ComptimeKeyword:
                    kind = FunctionKind.Comptime;
                    break;
                case TokenKind.Int1Keyword:
                    kind = FunctionKind.Int1;
                    break;
                case TokenKind.Int2Keyword:
                    kind = FunctionKind.Int2;
                    break;
                case TokenKind.Int3Keyword:
                    kind = FunctionKind.Int3;
                    break;
                case TokenKind.InlineKeyword:
                    inliningPolicy = FunctionInliningPolicy.ForceInline;
                    break;
                case TokenKind.NoinlineKeyword:
                    inliningPolicy = FunctionInliningPolicy.NeverInline;
                    break;
            }
        }

        return (kind, inliningPolicy);
    }

    private enum LoopContext
    {
        Regular,
        Rep,
    }
}
