using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    private readonly DiagnosticBag _diagnostics;
    private readonly Dictionary<string, TypeAliasSymbol> _typeAliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeSymbol> _resolvedTypeAliases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _typeAliasResolutionStack = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FunctionSymbol> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImportedModule> _importedModules = new(StringComparer.Ordinal);
    private readonly Dictionary<FunctionSymbol, BoundBlockStatement> _boundFunctionBodies = new();
    private readonly Dictionary<FunctionSymbol, ComptimeSupportResult> _comptimeSupportCache = new();
    private readonly Dictionary<Symbol, object?> _knownConstantValues = new();
    private readonly Dictionary<string, ImportedModuleDefinition> _moduleDefinitionCache;
    private readonly Dictionary<string, string> _namedModuleOwners;
    private readonly HashSet<string> _moduleBindingStack;
    private readonly Scope _globalScope;
    private readonly Scope _topLevelScope;
    private Scope _currentScope;
    private FunctionSymbol? _currentFunction;
    private readonly Stack<LoopContext> _loopStack = new();
    private readonly int _comptimeFuel;
    private int _anonymousStructIndex;

    private static readonly EnumTypeSymbol MemorySpaceType = new("MemorySpace", BuiltinTypes.U32,
        new Dictionary<string, long>(StringComparer.Ordinal) { ["reg"] = 0, ["lut"] = 1, ["hub"] = 2 },
        isOpen: false);

    private Binder(
        DiagnosticBag diagnostics,
        HashSet<string> moduleBindingStack,
        Dictionary<string, ImportedModuleDefinition> moduleDefinitionCache,
        Dictionary<string, string> namedModuleOwners,
        int comptimeFuel)
    {
        _diagnostics = diagnostics;
        _moduleBindingStack = Requires.NotNull(moduleBindingStack);
        _moduleDefinitionCache = Requires.NotNull(moduleDefinitionCache);
        _namedModuleOwners = Requires.NotNull(namedModuleOwners);
        _comptimeFuel = Requires.Positive(comptimeFuel);
        _globalScope = new Scope(parent: null);
        _topLevelScope = new Scope(_globalScope);
        _currentScope = _globalScope;
    }

    public static BoundProgram Bind(
        CompilationUnitSyntax unit,
        DiagnosticBag diagnostics,
        string rootFilePath,
        IReadOnlyDictionary<string, string>? namedModuleRoots,
        int comptimeFuel = 250)
    {
        Requires.NotNull(unit);
        Requires.NotNull(diagnostics);
        Requires.NotNull(rootFilePath);

        HashSet<string> moduleBindingStack = new(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(rootFilePath),
        };
        Dictionary<string, ImportedModuleDefinition> moduleDefinitionCache = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> namedModuleOwners = new(StringComparer.OrdinalIgnoreCase);
        Binder binder = new(diagnostics, moduleBindingStack, moduleDefinitionCache, namedModuleOwners, comptimeFuel);
        return binder.BindCompilationUnit(unit, Path.GetFullPath(rootFilePath), namedModuleRoots ?? new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private BoundProgram BindCompilationUnit(CompilationUnitSyntax unit, string rootFilePath, IReadOnlyDictionary<string, string> namedModuleRoots)
    {
        BindImports(unit, rootFilePath, namedModuleRoots);
        CollectTopLevelTypes(unit);
        CollectTopLevelFunctions(unit);
        ResolveFunctionSignatures();
        DeclareTopLevelVariables(unit);
        ResolveAllTypeAliases();

        List<BoundGlobalVariableMember> boundGlobals = new();
        List<BoundFunctionMember> boundFunctions = new();
        List<BoundStatement> boundTopLevelStatements = new();

        foreach (MemberSyntax member in unit.Members)
        {
            switch (member)
            {
                case FunctionDeclarationSyntax function:
                    boundFunctions.Add(BindFunction(function));
                    break;

                case AsmFunctionDeclarationSyntax asmFunction:
                    boundFunctions.Add(BindAsmFunction(asmFunction));
                    break;
            }
        }

        _currentScope = _topLevelScope;
        foreach (MemberSyntax member in unit.Members)
        {
            switch (member)
            {
                case TypeAliasDeclarationSyntax:
                    break;

                case ImportDeclarationSyntax:
                    break;

                case VariableDeclarationSyntax variable:
                    _currentScope = _globalScope;
                    boundGlobals.Add(BindGlobalVariable(variable));
                    _currentScope = _topLevelScope;
                    break;

                case GlobalStatementSyntax globalStatement:
                    if (BindStatementNullable(globalStatement.Statement, isTopLevel: true) is BoundStatement boundStatement)
                        boundTopLevelStatements.Add(boundStatement);
                    break;
            }
        }

        return new BoundProgram(
            boundTopLevelStatements,
            boundGlobals,
            boundFunctions,
            _resolvedTypeAliases,
            _functions,
            _importedModules);
    }


    private void BindImports(CompilationUnitSyntax unit, string importerFilePath, IReadOnlyDictionary<string, string> namedModuleRoots)
    {
        foreach (MemberSyntax member in unit.Members)
        {
            if (member is not ImportDeclarationSyntax import)
                continue;

            if (import.IsFileImport && import.Alias is null)
            {
                _diagnostics.ReportFileImportAliasRequired(import.Source.Span);
                continue;
            }

            string alias = import.Alias?.Text ?? import.Source.Text;
            string sourceName = import.Source.Value as string ?? import.Source.Text;
            string resolvedPath;
            if (import.IsFileImport)
            {
                string importerDir = Path.GetDirectoryName(importerFilePath) ?? string.Empty;
                resolvedPath = Path.GetFullPath(Path.Combine(importerDir, sourceName));
            }
            else if (string.Equals(sourceName, "builtin", StringComparison.Ordinal))
            {
                ImportedModule builtinModule = CreateBuiltinModule(alias);
                _importedModules[alias] = builtinModule;
                if (!_globalScope.TryDeclare(new ModuleSymbol(alias, builtinModule)))
                    _diagnostics.ReportSymbolAlreadyDeclared(import.Alias?.Span ?? import.Source.Span, alias);
                continue;
            }
            else
            {
                if (!namedModuleRoots.TryGetValue(sourceName, out string? namedModulePath))
                {
                    _diagnostics.ReportUnknownNamedModule(import.Source.Span, sourceName);
                    continue;
                }

                resolvedPath = Path.GetFullPath(namedModulePath);
                if (_namedModuleOwners.TryGetValue(resolvedPath, out string? ownerName))
                {
                    if (!string.Equals(ownerName, sourceName, StringComparison.Ordinal))
                    {
                        _diagnostics.ReportDuplicateNamedModuleRoot(import.Source.Span, ownerName, sourceName, resolvedPath);
                        continue;
                    }
                }
                else
                {
                    _namedModuleOwners[resolvedPath] = sourceName;
                }
            }

            ImportedModule imported = LoadAndBindModule(sourceName, alias, resolvedPath, namedModuleRoots);
            _importedModules[alias] = imported;
            if (!_globalScope.TryDeclare(new ModuleSymbol(alias, imported)))
                _diagnostics.ReportSymbolAlreadyDeclared(import.Alias?.Span ?? import.Source.Span, alias);
        }
    }

    private ImportedModule LoadAndBindModule(string sourceName, string alias, string resolvedPath, IReadOnlyDictionary<string, string> namedModuleRoots)
    {
        if (!File.Exists(resolvedPath))
        {
            _diagnostics.ReportImportFileNotFound(new TextSpan(0, 0), resolvedPath);
            return CreateEmptyImportedModule(sourceName, alias, resolvedPath);
        }

        if (_moduleDefinitionCache.TryGetValue(resolvedPath, out ImportedModuleDefinition? cachedDefinition))
            return cachedDefinition.CreateImport(sourceName, alias);

        bool sourceIsValid = SourceFileLoader.TryLoad(resolvedPath, _diagnostics, out SourceText source);
        if (!sourceIsValid)
            return CreateEmptyImportedModule(sourceName, alias, resolvedPath);

        Parser parser = Parser.Create(source, _diagnostics);
        CompilationUnitSyntax syntax = parser.ParseCompilationUnit();
        if (_moduleBindingStack.Contains(resolvedPath))
        {
            _diagnostics.ReportCircularImport(new TextSpan(0, 0), resolvedPath);
            return CreateEmptyImportedModule(sourceName, alias, resolvedPath, syntax);
        }

        _moduleBindingStack.Add(resolvedPath);
        BoundProgram program;
        try
        {
            Binder nestedBinder = new(_diagnostics, _moduleBindingStack, _moduleDefinitionCache, _namedModuleOwners, _comptimeFuel);
            program = nestedBinder.BindCompilationUnit(syntax, resolvedPath, namedModuleRoots);
        }
        finally
        {
            _moduleBindingStack.Remove(resolvedPath);
        }

        Dictionary<string, FunctionSymbol> functions = new(StringComparer.Ordinal);
        foreach ((string name, FunctionSymbol function) in program.FunctionLookup)
            functions[name] = function;

        Dictionary<string, TypeSymbol> types = new(StringComparer.Ordinal);
        foreach ((string name, TypeSymbol type) in program.TypeAliases)
            types[name] = type;

        Dictionary<string, VariableSymbol> variables = new(StringComparer.Ordinal);
        foreach (BoundGlobalVariableMember global in program.GlobalVariables)
            variables[global.Symbol.Name] = global.Symbol;

        Dictionary<string, ImportedModule> importedModules = new(StringComparer.Ordinal);
        foreach ((string name, ImportedModule importedModule) in program.ImportedModules)
            importedModules[name] = importedModule;

        ImportedModuleDefinition definition = new(resolvedPath, syntax, program, functions, types, variables, importedModules);
        _moduleDefinitionCache[resolvedPath] = definition;
        return definition.CreateImport(sourceName, alias);
    }

    private static ImportedModule CreateEmptyImportedModule(string sourceName, string alias, string resolvedPath, CompilationUnitSyntax? syntax = null)
    {
        CompilationUnitSyntax effectiveSyntax = syntax ?? new CompilationUnitSyntax([], new Token(TokenKind.EndOfFile, new TextSpan(0, 0), string.Empty));
        BoundProgram emptyProgram = new([], [], [], new Dictionary<string, TypeSymbol>(), new Dictionary<string, FunctionSymbol>(), new Dictionary<string, ImportedModule>());
        return new ImportedModule(
            sourceName,
            resolvedPath,
            alias,
            effectiveSyntax,
            emptyProgram,
            new Dictionary<string, FunctionSymbol>(),
            new Dictionary<string, TypeSymbol>(),
            new Dictionary<string, VariableSymbol>(),
            new Dictionary<string, ImportedModule>());
    }

    private static ImportedModule CreateBuiltinModule(string alias)
    {
        Dictionary<string, long> members = new(StringComparer.Ordinal)
        {
            ["reg"] = 0,
            ["lut"] = 1,
            ["hub"] = 2,
        };

        EnumTypeSymbol memorySpaceType = new("MemorySpace", BuiltinTypes.U32, members, isOpen: false);
        Dictionary<string, TypeSymbol> exportedTypes = new(StringComparer.Ordinal)
        {
            ["MemorySpace"] = memorySpaceType,
        };

        CompilationUnitSyntax emptySyntax = new([], new Token(TokenKind.EndOfFile, new TextSpan(0, 0), string.Empty));
        BoundProgram emptyProgram = new([], [], [], new Dictionary<string, TypeSymbol>(), new Dictionary<string, FunctionSymbol>(), new Dictionary<string, ImportedModule>());
        return new ImportedModule(
            "builtin",
            "<builtin>",
            alias,
            emptySyntax,
            emptyProgram,
            new Dictionary<string, FunctionSymbol>(),
            exportedTypes,
            new Dictionary<string, VariableSymbol>(),
            new Dictionary<string, ImportedModule>());
    }

    private void CollectTopLevelTypes(CompilationUnitSyntax unit)
    {
        foreach (MemberSyntax member in unit.Members)
        {
            if (member is not TypeAliasDeclarationSyntax typeAlias)
                continue;

            TypeAliasSymbol symbol = new(typeAlias.Name.Text, typeAlias);
            if (!_typeAliases.TryAdd(symbol.Name, symbol))
                _diagnostics.ReportSymbolAlreadyDeclared(typeAlias.Name.Span, symbol.Name);
        }
    }

    private void CollectTopLevelFunctions(CompilationUnitSyntax unit)
    {
        _currentScope = _globalScope;
        foreach (MemberSyntax member in unit.Members)
        {
            IFunctionSignatureSyntax syntax;
            FunctionKind kind;

            switch (member)
            {
                case FunctionDeclarationSyntax functionDecl:
                    syntax = functionDecl;
                    kind = GetFunctionKind(functionDecl.FuncKindKeyword?.Kind);
                    break;

                case AsmFunctionDeclarationSyntax asmFunctionDecl:
                    syntax = asmFunctionDecl;
                    kind = FunctionKind.Leaf;
                    break;

                default:
                    continue;
            }

            FunctionSymbol function = new(syntax.Name.Text, syntax, kind);
            if (!_functions.TryAdd(function.Name, function))
            {
                _diagnostics.ReportSymbolAlreadyDeclared(syntax.Name.Span, function.Name);
                continue;
            }

            _globalScope.TryDeclare(function);
        }
    }

    private void ResolveFunctionSignatures()
    {
        foreach ((_, FunctionSymbol function) in _functions)
        {
            List<ParameterSymbol> parameters = new();
            HashSet<string> parameterNames = new(StringComparer.Ordinal);

            foreach (ParameterSyntax param in function.Syntax.Parameters)
            {
                TypeSymbol parameterType = BindType(param.Type);
                if (param.StorageClassKeyword is Token storageClassKeyword)
                    _diagnostics.ReportInvalidParameterStorageClass(storageClassKeyword.Span, storageClassKeyword.Text);

                if (!parameterNames.Add(param.Name.Text))
                {
                    _diagnostics.ReportSymbolAlreadyDeclared(param.Name.Span, param.Name.Text);
                    continue;
                }

                parameters.Add(new ParameterSymbol(param.Name.Text, parameterType));
            }

            List<ReturnSlot> returnSlots = new();
            if (function.Syntax.ReturnSpec is not null)
            {
                foreach (ReturnItemSyntax returnItem in function.Syntax.ReturnSpec)
                {
                    TypeSymbol returnType = BindType(returnItem.Type);
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

            if (returnSlots.Count == 1 && returnSlots[0].Type.IsVoid)
                returnSlots.Clear();

            // Auto-assign flag placements for multi-return: slot 1 -> FlagC, slot 2 -> FlagZ
            // when the slot has no explicit annotation and is bool/bit.
            if (returnSlots.Count > 1)
            {
                ReturnPlacement nextFlag = ReturnPlacement.FlagC;
                for (int i = 1; i < returnSlots.Count; i++)
                {
                    ReturnSlot slot = returnSlots[i];
                    if (slot.Placement == ReturnPlacement.Register
                        && (slot.Type.IsBool || ReferenceEquals(slot.Type, BuiltinTypes.Bit)))
                    {
                        returnSlots[i] = new ReturnSlot(slot.Type, nextFlag);
                        nextFlag = nextFlag == ReturnPlacement.FlagC ? ReturnPlacement.FlagZ : nextFlag;
                    }
                }
            }

            function.Parameters = parameters;
            function.ReturnSlots = returnSlots;
        }
    }

    private void DeclareTopLevelVariables(CompilationUnitSyntax unit)
    {
        foreach (MemberSyntax member in unit.Members)
        {
            if (member is not VariableDeclarationSyntax variableDecl)
                continue;

            TypeSymbol variableType = BindType(variableDecl.Type);
            VariableSymbol symbol = CreateVariableSymbol(variableDecl, variableType, VariableScopeKind.GlobalStorage);

            if (!_globalScope.TryDeclare(symbol))
                _diagnostics.ReportSymbolAlreadyDeclared(variableDecl.Name.Span, variableDecl.Name.Text);
        }
    }

    private void ResolveAllTypeAliases()
    {
        foreach ((string aliasName, TypeAliasSymbol aliasSymbol) in _typeAliases)
            _ = ResolveTypeAlias(aliasName, aliasSymbol.Syntax.Name.Span);
    }

    private BoundGlobalVariableMember BindGlobalVariable(VariableDeclarationSyntax variable)
    {
        TypeSymbol variableType = BindType(variable.Type);
        VariableSymbol variableSymbol;
        if (_globalScope.TryLookup(variable.Name.Text, out Symbol? symbol) && symbol is VariableSymbol declaredVariable)
        {
            variableSymbol = declaredVariable;
        }
        else
        {
            variableSymbol = CreateVariableSymbol(variable, variableType, VariableScopeKind.GlobalStorage);
        }

        ResolveLayoutMetadata(variable, variableSymbol);

        BoundExpression? initializer = null;
        if (variable.Initializer is not null)
        {
            if (variableSymbol.IsExtern)
            {
                _diagnostics.ReportExternCannotHaveInitializer(variable.Initializer.Span, variableSymbol.Name);
            }
            else
            {
                initializer = BindExpression(variable.Initializer, variableSymbol.Type);
                if (RequiresComptimeInitializer(variableSymbol))
                    initializer = RequireComptimeExpression(initializer, variable.Initializer.Span);
            }
        }

        RememberKnownConstantValue(variableSymbol, initializer);

        return new BoundGlobalVariableMember(variableSymbol, initializer, variable.Span);
    }

    private BoundFunctionMember BindFunction(FunctionDeclarationSyntax functionSyntax)
    {
        bool found = _functions.TryGetValue(functionSyntax.Name.Text, out FunctionSymbol? function);
        Debug.Assert(found && function is not null, "CollectTopLevelFunctions must register every function before binding.");

        Scope previousScope = _currentScope;
        FunctionSymbol? previousFunction = _currentFunction;

        _currentScope = new Scope(_globalScope);
        _currentFunction = function;

        foreach (ParameterSymbol parameter in function.Parameters)
        {
            bool declared = _currentScope.TryDeclare(parameter);
            Debug.Assert(declared, "ResolveFunctionSignatures must deduplicate parameters before binding.");
        }

        BoundBlockStatement body = BindBlockStatement(functionSyntax.Body, createScope: false, isTopLevel: false);

        if (function.ReturnTypes.Count > 0 && !AlwaysReturns(body))
            _diagnostics.ReportMissingReturnValue(functionSyntax.Body.CloseBrace.Span, function.Name);

        _currentFunction = previousFunction;
        _currentScope = previousScope;
        _boundFunctionBodies[function] = body;

        return new BoundFunctionMember(function, body, functionSyntax.Span);
    }

    private BoundFunctionMember BindAsmFunction(AsmFunctionDeclarationSyntax asmSyntax)
    {
        bool found = _functions.TryGetValue(asmSyntax.Name.Text, out FunctionSymbol? function);
        Debug.Assert(found && function is not null, "CollectTopLevelFunctions must register every asm function before binding.");

        Scope previousScope = _currentScope;
        FunctionSymbol? previousFunction = _currentFunction;

        _currentScope = new Scope(_globalScope);
        _currentFunction = function;

        foreach (ParameterSymbol parameter in function.Parameters)
        {
            bool declared = _currentScope.TryDeclare(parameter);
            Debug.Assert(declared, "ResolveFunctionSignatures must deduplicate parameters before binding.");
        }

        // For asm fn, the "return" keyword is a valid binding name referencing the return value.
        // Add a synthetic variable so the validator accepts {return} references.
        // For flag-only returns (e.g. -> bool@C), no {return} variable is needed because
        // the return value lives in the flag, not in a register.
        VariableSymbol? returnSymbol = null;
        bool hasRegisterReturn = function.ReturnSlots.Any(s => s.Placement == ReturnPlacement.Register);
        if (hasRegisterReturn)
        {
            TypeSymbol firstRegisterType = function.ReturnSlots.First(s => s.Placement == ReturnPlacement.Register).Type;
            returnSymbol = new VariableSymbol(
                "return",
                firstRegisterType,
                isConst: false,
                VariableStorageClass.Automatic,
                VariableScopeKind.Local,
                isExtern: false,
                fixedAddress: null,
                alignment: null);
            _currentScope.TryDeclare(returnSymbol);
        }

        AsmVolatility volatility = asmSyntax.VolatileKeyword is not null
            ? AsmVolatility.Volatile
            : AsmVolatility.NonVolatile;

        // Determine flag output from return spec placement annotations.
        string? flagOutput = null;
        ReturnSlot? firstFlagSlot = function.ReturnSlots.Cast<ReturnSlot?>().FirstOrDefault(s => s!.Value.IsFlagPlaced);
        if (firstFlagSlot is not null)
        {
            flagOutput = firstFlagSlot.Value.Placement == ReturnPlacement.FlagC ? "@C" : "@Z";
        }

        Dictionary<string, Symbol> availableSymbols = CollectInlineAsmAvailableSymbols();
        HashSet<string> availableVars = new(availableSymbols.Keys, StringComparer.Ordinal);

        InlineAssemblyValidator.ValidationResult validationResult =
            InlineAssemblyValidator.Validate(asmSyntax.Body, asmSyntax.Span, availableVars, _diagnostics);

        Dictionary<string, Symbol> referencedSymbols = new(StringComparer.Ordinal);
        foreach (string name in validationResult.ReferencedVariables)
        {
            if (availableSymbols.TryGetValue(name, out Symbol? referenced))
                referencedSymbols[name] = referenced;
        }

        BoundAsmStatement asmStatement = new(volatility, asmSyntax.Body, flagOutput, validationResult.Lines, referencedSymbols, asmSyntax.Span);

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

        BoundBlockStatement body = new(bodyStatements, asmSyntax.Span);

        _currentFunction = previousFunction;
        _currentScope = previousScope;
        _boundFunctionBodies[function] = body;

        return new BoundFunctionMember(function, body, asmSyntax.Span);
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

    private BoundBlockStatement BindBlockStatement(BlockStatementSyntax block, bool createScope, bool isTopLevel)
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
            if (BindStatementNullable(statement, isTopLevel) is BoundStatement boundStatement)
                statements.Add(boundStatement);
        }

        if (createScope && previousScope is not null)
            _currentScope = previousScope;

        return new BoundBlockStatement(statements, block.Span);
    }

    private BoundStatement BindStatement(StatementSyntax statement, bool isTopLevel)
        => BindStatementNullable(statement, isTopLevel) ?? new BoundBlockStatement([], statement.Span);

    private BoundStatement? BindStatementNullable(StatementSyntax statement, bool isTopLevel)
    {
        switch (statement)
        {
            case BlockStatementSyntax block:
                return BindBlockStatement(block, createScope: true, isTopLevel: false);

            case VariableDeclarationStatementSyntax variableDeclStatement:
                return BindLocalVariableDeclaration(variableDeclStatement.Declaration);

            case ExpressionStatementSyntax expressionStatement:
                return new BoundExpressionStatement(BindExpression(expressionStatement.Expression), expressionStatement.Span);

            case AssignmentStatementSyntax assignment:
            {
                BoundAssignmentTarget target = BindAssignmentTarget(assignment.Target);
                BoundExpression value = BindExpression(assignment.Value, target.Type);
                return new BoundAssignmentStatement(target, value, assignment.Operator.Kind, assignment.Span);
            }

            case MultiAssignmentStatementSyntax multiAssignment:
                return BindMultiAssignmentStatement(multiAssignment);

            case IfStatementSyntax ifStatement:
            {
                BoundExpression condition = BindExpression(ifStatement.Condition, BuiltinTypes.Bool);
                BoundStatement thenBody = ifStatement.ThenBody switch
                {
                    BlockStatementSyntax thenBlock => BindBlockStatement(thenBlock, createScope: true, isTopLevel: false),
                    _ => BindStatement(ifStatement.ThenBody, isTopLevel: false),
                };
                BoundStatement? elseBody = null;
                if (ifStatement.ElseClause is not null)
                {
                    elseBody = ifStatement.ElseClause.Body switch
                    {
                        BlockStatementSyntax elseBlock => BindBlockStatement(elseBlock, createScope: true, isTopLevel: false),
                        _ => BindStatement(ifStatement.ElseClause.Body, isTopLevel: false),
                    };
                }

                return new BoundIfStatement(condition, thenBody, elseBody, ifStatement.Span);
            }

            case WhileStatementSyntax whileStatement:
            {
                BoundExpression condition = BindExpression(whileStatement.Condition, BuiltinTypes.Bool);
                PushLoop(LoopContext.Regular);
                BoundBlockStatement body = BindBlockStatement(whileStatement.Body, createScope: true, isTopLevel: false);
                PopLoop();
                return new BoundWhileStatement(condition, body, whileStatement.Span);
            }

            case ForStatementSyntax forStatement:
                return BindForStatement(forStatement);

            case LoopStatementSyntax loopStatement:
            {
                PushLoop(LoopContext.Regular);
                BoundBlockStatement body = BindBlockStatement(loopStatement.Body, createScope: true, isTopLevel: false);
                PopLoop();
                return new BoundLoopStatement(body, loopStatement.Span);
            }

            case RepLoopStatementSyntax repLoop:
            {
                PushLoop(LoopContext.Rep);
                BoundBlockStatement body = BindBlockStatement(repLoop.Body, createScope: true, isTopLevel: false);
                PopLoop();
                return new BoundRepLoopStatement(body, repLoop.Span);
            }

            case RepForStatementSyntax repFor:
            {
                // The iterable should be a range expression for rep for
                BoundExpression iterable = BindExpression(repFor.Iterable);
                BoundExpression start;
                BoundExpression end;
                if (repFor.Iterable is RangeExpressionSyntax range)
                {
                    start = BindExpression(range.Start);
                    end = BindExpression(range.End);
                    if (!start.Type.IsInteger)
                        _diagnostics.ReportTypeMismatch(range.Start.Span, "integer", start.Type.Name);
                    if (!end.Type.IsInteger)
                        _diagnostics.ReportTypeMismatch(range.End.Span, "integer", end.Type.Name);
                }
                else
                {
                    // Non-range iterable — use as count (0..count)
                    start = new BoundLiteralExpression(0L, repFor.Iterable.Span, BuiltinTypes.U32);
                    end = iterable;
                }

                PushLoop(LoopContext.Rep);
                BoundRepForStatement boundRepFor = BindRepForBody(repFor, start, end);
                PopLoop();
                return boundRepFor;
            }

            case NoirqStatementSyntax noirq:
            {
                BoundBlockStatement body = BindBlockStatement(noirq.Body, createScope: true, isTopLevel: false);
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
                    _diagnostics.ReportInvalidYield(yieldStatement.YieldKeyword.Span);
                }

                return new BoundYieldStatement(yieldStatement.Span);
            }

            case YieldtoStatementSyntax yieldtoStatement:
                return BindYieldtoStatement(yieldtoStatement, isTopLevel);

            case AsmBlockStatementSyntax asm:
            {
                string? flagOutput = null;
                VariableSymbol? outputSymbol = null;
                if (asm.OutputBinding is not null)
                {
                    string flag = asm.OutputBinding.FlagAnnotation?.Flag.Text ?? "";
                    if (flag is not ("C" or "Z"))
                    {
                        _diagnostics.ReportInlineAsmInvalidFlagOutput(asm.OutputBinding.Span, flag);
                    }

                    flagOutput = $"@{flag}";
                    TypeSymbol outputType = BindType(asm.OutputBinding.Type);
                    VariableScopeKind outputScopeKind = _currentFunction is null
                        ? VariableScopeKind.TopLevelAutomatic
                        : VariableScopeKind.Local;
                    outputSymbol = new VariableSymbol(
                        asm.OutputBinding.Name.Text,
                        outputType,
                        isConst: false,
                        VariableStorageClass.Automatic,
                        outputScopeKind,
                        isExtern: false,
                        fixedAddress: null,
                        alignment: null);

                    if (!_currentScope.TryDeclare(outputSymbol))
                        _diagnostics.ReportSymbolAlreadyDeclared(asm.OutputBinding.Name.Span, asm.OutputBinding.Name.Text);
                }

                Dictionary<string, Symbol> availableSymbols = CollectInlineAsmAvailableSymbols();
                HashSet<string> availableVars = new(availableSymbols.Keys, StringComparer.Ordinal);

                InlineAssemblyValidator.ValidationResult validationResult =
                    InlineAssemblyValidator.Validate(asm.Body, asm.Span, availableVars, _diagnostics);

                Dictionary<string, Symbol> referencedSymbols = new(StringComparer.Ordinal);
                foreach (string name in validationResult.ReferencedVariables)
                {
                    if (availableSymbols.TryGetValue(name, out Symbol? referenced))
                        referencedSymbols[name] = referenced;
                }

                if (outputSymbol is not null)
                    referencedSymbols[outputSymbol.Name] = outputSymbol;

                return new BoundAsmStatement(asm.Volatility, asm.Body, flagOutput, validationResult.Lines, referencedSymbols, asm.Span);
            }
        }

        return new BoundErrorStatement(statement.Span);
    }

    private BoundStatement? BindAssertStatement(AssertStatementSyntax assertStatement)
    {
        if (assertStatement.CommaToken is not null && assertStatement.MessageLiteral is null)
            return new BoundErrorStatement(assertStatement.Span);

        BoundExpression condition = BindExpression(assertStatement.Condition, BuiltinTypes.Bool);

        if (condition is BoundErrorExpression)
            return new BoundErrorStatement(assertStatement.Span);

        if (!TryEvaluateAssertCondition(condition, out object? value, out ComptimeFailure failure))
        {
            if (!ContainsErrorExpression(condition))
            {
                if (failure.Kind is ComptimeFailureKind.NotEvaluable or ComptimeFailureKind.ForbiddenSymbolAccess)
                    _diagnostics.ReportComptimeValueRequired(assertStatement.Condition.Span);
                else
                    ReportComptimeFailure(failure);
            }

            return new BoundErrorStatement(assertStatement.Span);
        }

        if (value is not bool conditionValue)
        {
            _diagnostics.ReportTypeMismatch(assertStatement.Condition.Span, "bool", condition.Type.Name);
            return new BoundErrorStatement(assertStatement.Span);
        }

        if (conditionValue)
            return null;

        _diagnostics.ReportAssertionFailed(assertStatement.Span, assertStatement.MessageLiteral?.Value as string);
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

        if (_currentFunction is not null)
        {
            foreach (ParameterSymbol param in _currentFunction.Parameters)
                availableSymbols[param.Name] = param;
        }

        foreach ((string moduleAlias, ImportedModule module) in _importedModules)
        {
            _ = availableSymbols.TryAdd(moduleAlias, new ModuleSymbol(moduleAlias, module));
            CollectImportedModuleSymbols(moduleAlias, module, availableSymbols);
        }

        return availableSymbols;
    }

    private static void CollectImportedModuleSymbols(string prefix, ImportedModule module, IDictionary<string, Symbol> symbols)
    {
        foreach ((string name, VariableSymbol variable) in module.ExportedVariables)
            symbols[$"{prefix}.{name}"] = variable;

        foreach ((string alias, ImportedModule nestedModule) in module.ImportedModules)
        {
            string nestedPrefix = $"{prefix}.{alias}";
            symbols[nestedPrefix] = new ModuleSymbol(alias, nestedModule);
            CollectImportedModuleSymbols(nestedPrefix, nestedModule, symbols);
        }
    }

    private BoundVariableDeclarationStatement BindLocalVariableDeclaration(VariableDeclarationSyntax declaration)
    {
        TypeSymbol variableType = BindType(declaration.Type);
        VariableScopeKind scopeKind = _currentFunction is null
            ? VariableScopeKind.TopLevelAutomatic
            : VariableScopeKind.Local;
        VariableSymbol variableSymbol = CreateVariableSymbol(declaration, variableType, scopeKind);
        if (!_currentScope.TryDeclare(variableSymbol))
        {
            _diagnostics.ReportSymbolAlreadyDeclared(declaration.Name.Span, declaration.Name.Text);
            return new BoundVariableDeclarationStatement(variableSymbol, initializer: null, declaration.Span);
        }

        BoundExpression? initializer = null;
        if (declaration.Initializer is not null)
            initializer = BindExpression(declaration.Initializer, variableType);

        RememberKnownConstantValue(variableSymbol, initializer);

        return new BoundVariableDeclarationStatement(variableSymbol, initializer, declaration.Span);
    }

    private BoundStatement BindForStatement(ForStatementSyntax forStatement)
    {
        BoundExpression iterable = BindExpression(forStatement.Iterable);
        ForBindingSyntax? binding = forStatement.Binding;
        bool isArrayIteration = iterable.Type is ArrayTypeSymbol;
        bool isIntegerIteration = iterable.Type.IsInteger || iterable.Type == BuiltinTypes.IntegerLiteral;

        VariableSymbol? itemVariable = null;
        bool itemIsMutable = false;
        VariableSymbol? indexVariable = null;

        Scope previousScope = _currentScope;
        _currentScope = new Scope(previousScope);

        if (binding is not null)
        {
            itemIsMutable = binding.Ampersand is not null;

            if (isArrayIteration)
            {
                ArrayTypeSymbol arrayType = (ArrayTypeSymbol)iterable.Type;
                TypeSymbol elementType = arrayType.ElementType;

                // Item variable — const or mutable alias to the element
                itemVariable = new VariableSymbol(
                    binding.ItemName.Text,
                    elementType,
                    isConst: !itemIsMutable,
                    VariableStorageClass.Automatic,
                    VariableScopeKind.Local,
                    isExtern: false,
                    fixedAddress: null,
                    alignment: null);
                _currentScope.TryDeclare(itemVariable);

                // Index variable — user-provided or synthetic
                string indexName = binding.IndexName?.Text ?? "__for_index";
                indexVariable = new VariableSymbol(
                    indexName,
                    BuiltinTypes.U32,
                    isConst: true,
                    VariableStorageClass.Automatic,
                    VariableScopeKind.Local,
                    isExtern: false,
                    fixedAddress: null,
                    alignment: null);
                _currentScope.TryDeclare(indexVariable);
            }
            else if (isIntegerIteration)
            {
                // for(count) -> index: the binding variable is an index
                if (itemIsMutable)
                {
                    _diagnostics.ReportTypeMismatch(binding.Ampersand!.Value.Span, "array", iterable.Type.Name);
                }

                indexVariable = new VariableSymbol(
                    binding.ItemName.Text,
                    BuiltinTypes.U32,
                    isConst: true,
                    VariableStorageClass.Automatic,
                    VariableScopeKind.Local,
                    isExtern: false,
                    fixedAddress: null,
                    alignment: null);
                _currentScope.TryDeclare(indexVariable);
            }
            else
            {
                _diagnostics.ReportTypeMismatch(forStatement.Iterable.Span, "integer or array", iterable.Type.Name);
            }
        }
        else if (isIntegerIteration || isArrayIteration)
        {
            // No binding — create synthetic index for internal counting
            indexVariable = new VariableSymbol(
                "__for_index",
                BuiltinTypes.U32,
                isConst: true,
                VariableStorageClass.Automatic,
                VariableScopeKind.Local,
                isExtern: false,
                fixedAddress: null,
                alignment: null);
            _currentScope.TryDeclare(indexVariable);
        }
        else if (!iterable.Type.IsUnknown)
        {
            _diagnostics.ReportTypeMismatch(forStatement.Iterable.Span, "integer or array", iterable.Type.Name);
        }

        PushLoop(LoopContext.Regular);
        BoundBlockStatement body = BindBlockStatement(forStatement.Body, createScope: false, isTopLevel: false);
        PopLoop();

        _currentScope = previousScope;
        return new BoundForStatement(iterable, itemVariable, itemIsMutable, indexVariable, body, forStatement.Span);
    }

    private BoundRepForStatement BindRepForBody(RepForStatementSyntax repFor, BoundExpression start, BoundExpression end)
    {
        Scope previousScope = _currentScope;
        _currentScope = new Scope(previousScope);

        string variableName = repFor.Binding?.ItemName.Text ?? "__rep_index";
        VariableSymbol variable = new(
            variableName,
            BuiltinTypes.IntegerLiteral,
            isConst: true,
            VariableStorageClass.Automatic,
            VariableScopeKind.Local,
            isExtern: false,
            fixedAddress: null,
            alignment: null);
        _currentScope.TryDeclare(variable);

        BoundBlockStatement body = BindBlockStatement(repFor.Body, createScope: false, isTopLevel: false);

        _currentScope = previousScope;
        return new BoundRepForStatement(variable, start, end, body, repFor.Span);
    }

    private BoundStatement BindReturnStatement(ReturnStatementSyntax returnStatement)
    {
        List<BoundExpression> values = new();

        if (_currentFunction is null)
        {
            _diagnostics.ReportReturnOutsideFunction(returnStatement.ReturnKeyword.Span);
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
            _diagnostics.ReportReturnValueCountMismatch(
                returnStatement.ReturnKeyword.Span,
                _currentFunction.Name,
                expectedCount,
                actualCount);
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
        BoundExpression rhs = BindExpression(multiAssignment.Value);
        if (rhs is not BoundCallExpression call)
        {
            _diagnostics.ReportMultiAssignmentRequiresCall(multiAssignment.Value.Span);
            // Bind targets anyway to get diagnostics flowing
            foreach (ExpressionSyntax target in multiAssignment.Targets)
                BindAssignmentTarget(target);
            return new BoundExpressionStatement(rhs, multiAssignment.Span);
        }

        FunctionSymbol function = call.Function;
        IReadOnlyList<TypeSymbol> returnTypes = function.ReturnTypes;
        int targetCount = multiAssignment.Targets.Count;
        if (targetCount != returnTypes.Count)
        {
            _diagnostics.ReportMultiAssignmentTargetCountMismatch(
                multiAssignment.Operator.Span, function.Name, returnTypes.Count, targetCount);
        }

        List<BoundAssignmentTarget> targets = new();
        int count = Math.Min(targetCount, returnTypes.Count);
        for (int i = 0; i < count; i++)
        {
            ExpressionSyntax targetSyntax = multiAssignment.Targets[i];
            TypeSymbol expectedType = returnTypes[i];

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

        return new BoundMultiAssignmentStatement(targets, call, multiAssignment.Span);
    }

    private BoundStatement BindBreakOrContinueStatement(Token keywordToken, bool isBreak)
    {
        if (_loopStack.Count == 0)
        {
            _diagnostics.ReportInvalidLoopControl(keywordToken.Span, keywordToken.Text);
        }
        else if (isBreak && _loopStack.Peek() == LoopContext.Rep)
        {
            _diagnostics.ReportInvalidBreakInRep(keywordToken.Span);
        }

        return isBreak ? new BoundBreakStatement(keywordToken.Span) : new BoundContinueStatement(keywordToken.Span);
    }

    private BoundYieldtoStatement BindYieldtoStatement(YieldtoStatementSyntax yieldtoStatement, bool isTopLevel)
    {
        bool allowedContext = isTopLevel || (_currentFunction?.Kind == FunctionKind.Coro);
        if (!allowedContext)
            _diagnostics.ReportInvalidYieldto(yieldtoStatement.YieldtoKeyword.Span);

        FunctionSymbol? target = null;
        if (_functions.TryGetValue(yieldtoStatement.Target.Text, out FunctionSymbol? targetFunction))
        {
            target = targetFunction;
            if (target.Kind != FunctionKind.Coro)
                _diagnostics.ReportInvalidYieldtoTarget(yieldtoStatement.Target.Span, yieldtoStatement.Target.Text);
        }
        else
        {
            _diagnostics.ReportUndefinedName(yieldtoStatement.Target.Span, yieldtoStatement.Target.Text);
        }

        List<BoundExpression> arguments = target is null
            ? BindArgumentsLoose(yieldtoStatement.Arguments)
            : BindCallArguments(target, yieldtoStatement.Arguments, yieldtoStatement.Target.Span);

        return new BoundYieldtoStatement(target, arguments, yieldtoStatement.Span);
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
                    ImportedModule module = moduleType.Module.Module;
                    if (module.ExportedVariables.TryGetValue(memberAccess.Member.Text, out VariableSymbol? variable))
                        return new BoundSymbolAssignmentTarget(variable, target.Span, variable.Type);

                    _diagnostics.ReportUndefinedName(memberAccess.Member.Span, memberAccess.Member.Text);
                    return new BoundSymbolAssignmentTarget(
                        new VariableSymbol(
                            memberAccess.Member.Text,
                            BuiltinTypes.Unknown,
                            isConst: false,
                            VariableStorageClass.Automatic,
                            VariableScopeKind.Local,
                            isExtern: false,
                            fixedAddress: null,
                            alignment: null),
                        target.Span,
                        BuiltinTypes.Unknown);
                }

                TypeSymbol type = BuiltinTypes.Unknown;
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
                    _diagnostics.ReportUndefinedName(memberAccess.Member.Span, memberAccess.Member.Text);
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
                if (!indexExpr.Type.IsInteger)
                    _diagnostics.ReportTypeMismatch(index.Index.Span, "integer", indexExpr.Type.Name);

                TypeSymbol type = expression.Type switch
                {
                    ArrayTypeSymbol array => array.ElementType,
                    MultiPointerTypeSymbol pointer => pointer.PointeeType,
                    _ => BuiltinTypes.Unknown,
                };

                if (expression.Type is PointerTypeSymbol)
                    _diagnostics.ReportTypeMismatch(index.Expression.Span, "array or [*]pointer", expression.Type.Name);

                return new BoundIndexAssignmentTarget(expression, indexExpr, target.Span, type);
            }

            case PointerDerefExpressionSyntax pointerDeref:
            {
                BoundExpression expression = BindExpression(pointerDeref.Expression);
                TypeSymbol type = BindPointerDerefType(pointerDeref.Expression.Span, expression.Type);
                return new BoundPointerDerefAssignmentTarget(expression, target.Span, type);
            }

            default:
                _diagnostics.ReportInvalidAssignmentTarget(target.Span);
                return new BoundErrorAssignmentTarget(target.Span);
        }
    }

    private BoundAssignmentTarget BindNameAssignmentTarget(NameExpressionSyntax nameExpression, TypeSymbol? expectedType = null)
    {
        if (nameExpression.Name.Text == "_")
            return new BoundDiscardAssignmentTarget(nameExpression.Span, expectedType ?? BuiltinTypes.Unknown);

        if (!_currentScope.TryLookup(nameExpression.Name.Text, out Symbol? symbol) || symbol is null)
        {
            _diagnostics.ReportUndefinedName(nameExpression.Name.Span, nameExpression.Name.Text);
            return new BoundErrorAssignmentTarget(nameExpression.Span);
        }

        if (symbol is FunctionSymbol)
        {
            _diagnostics.ReportInvalidAssignmentTarget(nameExpression.Span);
            return new BoundErrorAssignmentTarget(nameExpression.Span);
        }

        if (symbol is VariableSymbol variable)
        {
            if (variable.IsConst)
                _diagnostics.ReportCannotAssignToConstant(nameExpression.Name.Span, nameExpression.Name.Text);
            return new BoundSymbolAssignmentTarget(symbol, nameExpression.Span, variable.Type);
        }

        if (symbol is ParameterSymbol parameter)
            return new BoundSymbolAssignmentTarget(symbol, nameExpression.Span, parameter.Type);

        _diagnostics.ReportInvalidAssignmentTarget(nameExpression.Span);
        return new BoundErrorAssignmentTarget(nameExpression.Span);
    }

    private BoundExpression BindExpression(ExpressionSyntax expression, TypeSymbol? expectedType = null)
    {
        BoundExpression bound = BindExpressionCore(expression, expectedType);
        if (expectedType is not null)
            bound = BindConversion(bound, expectedType, expression.Span, reportMismatch: true);

        return TryFoldExpression(bound, reportDiagnostics: false, out BoundExpression folded)
            ? folded
            : bound;
    }

    private BoundExpression BindExpressionCore(ExpressionSyntax expression, TypeSymbol? expectedType)
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

            case PostfixUnaryExpressionSyntax postfixUnary:
                return BindPostfixUnaryExpression(postfixUnary);

            case MemberAccessExpressionSyntax memberAccess:
                return BindMemberAccessExpression(memberAccess);

            case PointerDerefExpressionSyntax pointerDeref:
                return BindPointerDerefExpression(pointerDeref);

            case IndexExpressionSyntax index:
                return BindIndexExpression(index);

            case CallExpressionSyntax call:
                return BindCallExpression(call);

            case IntrinsicCallExpressionSyntax intrinsic:
                return BindIntrinsicCallExpression(intrinsic);

            case ArrayLiteralExpressionSyntax arrayLiteral:
                return BindArrayLiteralExpression(arrayLiteral, expectedType);

            case StructLiteralExpressionSyntax structLiteral:
                return BindStructLiteralExpression(structLiteral, expectedType);

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
        }

        return new BoundErrorExpression(expression.Span);
    }

    private BoundExpression BindLiteralExpression(LiteralExpressionSyntax literal)
    {
        TypeSymbol type = literal.Token.Kind switch
        {
            TokenKind.TrueKeyword or TokenKind.FalseKeyword => BuiltinTypes.Bool,
            TokenKind.StringLiteral or TokenKind.ZeroTerminatedStringLiteral => BuiltinTypes.String,
            TokenKind.UndefinedKeyword => BuiltinTypes.UndefinedLiteral,
            TokenKind.IntegerLiteral or TokenKind.CharLiteral => BuiltinTypes.IntegerLiteral,
            _ => BuiltinTypes.Unknown,
        };

        object? value = literal.Token.Value;
        if (value is null)
        {
            value = literal.Token.Kind switch
            {
                TokenKind.TrueKeyword => true,
                TokenKind.FalseKeyword => false,
                _ => value,
            };
        }

        // For zero-terminated strings, append NUL to the stored value
        if (literal.Token.Kind == TokenKind.ZeroTerminatedStringLiteral && value is string zStr)
            value = zStr + "\0";

        return new BoundLiteralExpression(value, literal.Span, type);
    }

    private BoundExpression BindNameExpression(NameExpressionSyntax nameExpression)
    {
        if (nameExpression.Name.Text == "_")
        {
            _diagnostics.ReportDiscardInExpression(nameExpression.Name.Span);
            return new BoundErrorExpression(nameExpression.Span);
        }

        if (!_currentScope.TryLookup(nameExpression.Name.Text, out Symbol? symbol) || symbol is null)
        {
            _diagnostics.ReportUndefinedName(nameExpression.Name.Span, nameExpression.Name.Text);
            return new BoundErrorExpression(nameExpression.Span);
        }

        if (symbol is VariableSymbol variable)
            return new BoundSymbolExpression(symbol, nameExpression.Span, variable.Type);
        if (symbol is ParameterSymbol parameter)
            return new BoundSymbolExpression(symbol, nameExpression.Span, parameter.Type);
        if (symbol is FunctionSymbol function)
            return new BoundSymbolExpression(symbol, nameExpression.Span, new FunctionTypeSymbol(function));

        Debug.Assert(symbol is ModuleSymbol, "Name expressions should only resolve to variables, parameters, functions, or modules.");
        ModuleSymbol module = (ModuleSymbol)Requires.NotNull(symbol as ModuleSymbol);
        return new BoundSymbolExpression(module, nameExpression.Span, new ModuleTypeSymbol(module));
    }

    private BoundExpression BindUnaryExpression(UnaryExpressionSyntax unary)
    {
        if (unary.Operator.Kind == TokenKind.Star)
        {
            BoundExpression operand = BindExpression(unary.Operand);
            TypeSymbol type = BindPointerDerefType(unary.Operand.Span, operand.Type);
            return new BoundPointerDerefExpression(operand, unary.Span, type);
        }

        BoundUnaryOperator? op = BoundUnaryOperator.Bind(unary.Operator.Kind);
        Debug.Assert(op is not null, "Parser should only produce unary operators that the binder understands.");
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
                if (!operand.Type.IsInteger)
                    _diagnostics.ReportTypeMismatch(unary.Operand.Span, "integer", operand.Type.Name);
                return new BoundUnaryExpression(unaryOperator, operand, unary.Span, operand.Type.IsInteger ? operand.Type : BuiltinTypes.Unknown);
            }

            case BoundUnaryOperatorKind.AddressOf:
                return BindAddressOfExpression(unary, unaryOperator);
        }

        Debug.Assert(
            unaryOperator.Kind is BoundUnaryOperatorKind.PostIncrement or BoundUnaryOperatorKind.PostDecrement,
            "BindUnaryExpression should only see prefix unary operators.");
        return new BoundErrorExpression(unary.Span);
    }

    private BoundExpression BindAddressOfExpression(UnaryExpressionSyntax unary, BoundUnaryOperator op)
    {
        if (unary.Operand is IndexExpressionSyntax indexExpression)
            return BindAddressOfIndexedElement(unary, op, indexExpression);

        if (unary.Operand is not NameExpressionSyntax nameExpression)
        {
            _diagnostics.ReportInvalidAddressOfTarget(unary.Operand.Span);
            _ = BindExpression(unary.Operand);
            return new BoundErrorExpression(unary.Span);
        }

        if (!_currentScope.TryLookup(nameExpression.Name.Text, out Symbol? symbol) || symbol is null)
        {
            _diagnostics.ReportUndefinedName(nameExpression.Name.Span, nameExpression.Name.Text);
            return new BoundErrorExpression(unary.Span);
        }

        if (symbol is VariableSymbol variable)
            return BindAddressOfVariable(unary, op, variable);
        if (symbol is ParameterSymbol parameter)
            return BindAddressOfParameter(unary, op, parameter);

        Debug.Assert(symbol is VariableSymbol or ParameterSymbol, "Address-of should only resolve to variables or parameters after lookup succeeds.");
        _diagnostics.ReportInvalidAddressOfTarget(unary.Operand.Span);
        return new BoundErrorExpression(unary.Span);
    }

    private BoundExpression BindAddressOfIndexedElement(UnaryExpressionSyntax unary, BoundUnaryOperator op, IndexExpressionSyntax indexExpression)
    {
        BoundExpression bound = BindIndexExpression(indexExpression);
        Debug.Assert(bound is BoundIndexExpression, "Indexed address-of should bind through the normal index-expression path.");
        BoundIndexExpression index = Requires.NotNull(bound as BoundIndexExpression);

        if (_currentFunction?.Kind == FunctionKind.Rec && TryGetRecursiveAddressOfName(index.Expression, out string? recursiveName))
        {
            _diagnostics.ReportAddressOfRecursiveLocal(unary.Operand.Span, Requires.NotNull(recursiveName));
            return new BoundErrorExpression(unary.Span);
        }

        if (!TryGetIndexedAddressType(index.Expression, index.Type, out PointerTypeSymbol? pointerType))
            return new BoundErrorExpression(unary.Span);

        return new BoundUnaryExpression(op, index, unary.Span, Requires.NotNull(pointerType));
    }

    private BoundExpression BindAddressOfVariable(UnaryExpressionSyntax unary, BoundUnaryOperator op, VariableSymbol variable)
    {
        if (_currentFunction?.Kind == FunctionKind.Rec && variable.ScopeKind == VariableScopeKind.Local)
        {
            _diagnostics.ReportAddressOfRecursiveLocal(unary.Operand.Span, variable.Name);
            return new BoundErrorExpression(unary.Span);
        }

        VariableStorageClass storageClass = variable.IsAutomatic
            ? VariableStorageClass.Reg
            : variable.StorageClass;
        TypeSymbol pointerType = variable.Type is ArrayTypeSymbol arrayType
            ? new MultiPointerTypeSymbol(arrayType.ElementType, variable.IsConst, storageClass: storageClass)
            : new PointerTypeSymbol(variable.Type, variable.IsConst, storageClass: storageClass);
        BoundSymbolExpression operand = new(variable, unary.Operand.Span, variable.Type);
        return new BoundUnaryExpression(op, operand, unary.Span, pointerType);
    }

    private BoundExpression BindAddressOfParameter(UnaryExpressionSyntax unary, BoundUnaryOperator op, ParameterSymbol parameter)
    {
        if (_currentFunction?.Kind == FunctionKind.Rec)
        {
            _diagnostics.ReportAddressOfRecursiveLocal(unary.Operand.Span, parameter.Name);
            return new BoundErrorExpression(unary.Span);
        }

        TypeSymbol pointerType = parameter.Type is ArrayTypeSymbol arrayType
            ? new MultiPointerTypeSymbol(arrayType.ElementType, isConst: false, storageClass: VariableStorageClass.Reg)
            : new PointerTypeSymbol(parameter.Type, isConst: false, storageClass: VariableStorageClass.Reg);
        BoundSymbolExpression operand = new(parameter, unary.Operand.Span, parameter.Type);
        return new BoundUnaryExpression(op, operand, unary.Span, pointerType);
    }

    private static bool TryGetRecursiveAddressOfName(BoundExpression expression, out string? name)
    {
        switch (expression)
        {
            case BoundSymbolExpression { Symbol: VariableSymbol { ScopeKind: VariableScopeKind.Local, Type: ArrayTypeSymbol } variable }:
                name = variable.Name;
                return true;

            case BoundSymbolExpression { Symbol: ParameterSymbol { Type: ArrayTypeSymbol } parameter }:
                name = parameter.Name;
                return true;

            default:
                name = null;
                return false;
        }
    }

    private static bool TryGetIndexedAddressType(BoundExpression expression, TypeSymbol elementType, out PointerTypeSymbol? pointerType)
    {
        if (expression.Type is MultiPointerTypeSymbol manyPointer)
        {
            pointerType = new PointerTypeSymbol(
                elementType,
                manyPointer.IsConst,
                manyPointer.IsVolatile,
                manyPointer.Alignment,
                manyPointer.StorageClass);
            return true;
        }

        switch (expression)
        {
            case BoundSymbolExpression { Symbol: VariableSymbol variable } when variable.Type is ArrayTypeSymbol:
            {
                VariableStorageClass storageClass = variable.IsAutomatic
                    ? VariableStorageClass.Reg
                    : variable.StorageClass;
                pointerType = new PointerTypeSymbol(elementType, variable.IsConst, storageClass: storageClass);
                return true;
            }

            case BoundSymbolExpression { Symbol: ParameterSymbol parameter } when parameter.Type is ArrayTypeSymbol:
                pointerType = new PointerTypeSymbol(elementType, isConst: false, storageClass: VariableStorageClass.Reg);
                return true;

            default:
                pointerType = null;
                return false;
        }
    }

    private BoundExpression BindPostfixUnaryExpression(PostfixUnaryExpressionSyntax postfixUnary)
    {
        BoundAssignmentTarget target = BindAssignmentTarget(postfixUnary.Operand);
        if (!target.Type.IsInteger)
            _diagnostics.ReportTypeMismatch(postfixUnary.Operand.Span, "integer", target.Type.Name);

        BoundExpression operandExpression = target switch
        {
            BoundSymbolAssignmentTarget symbol => new BoundSymbolExpression(symbol.Symbol, postfixUnary.Operand.Span, symbol.Type),
            BoundMemberAssignmentTarget member => new BoundMemberAccessExpression(member.Receiver, member.Member, postfixUnary.Operand.Span),
            BoundBitfieldAssignmentTarget bitfield => new BoundMemberAccessExpression(bitfield.ReceiverValue, bitfield.Member, postfixUnary.Operand.Span),
            BoundIndexAssignmentTarget index => new BoundIndexExpression(index.Expression, index.Index, postfixUnary.Operand.Span, index.Type),
            BoundPointerDerefAssignmentTarget deref => new BoundPointerDerefExpression(deref.Expression, postfixUnary.Operand.Span, deref.Type),
            _ => new BoundErrorExpression(postfixUnary.Operand.Span),
        };

        BoundUnaryOperatorKind kind = postfixUnary.Operator.Kind == TokenKind.PlusPlus
            ? BoundUnaryOperatorKind.PostIncrement
            : BoundUnaryOperatorKind.PostDecrement;

        return new BoundUnaryExpression(
            new BoundUnaryOperator(postfixUnary.Operator.Kind, kind),
            operandExpression,
            postfixUnary.Span,
            target.Type);
    }

    private BoundExpression BindBinaryExpression(BinaryExpressionSyntax binary)
    {
        BoundBinaryOperator? op = BoundBinaryOperator.Bind(binary.Operator.Kind);
        Debug.Assert(op is not null, "Parser should only produce binary operators that the binder understands.");
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

        if (binaryOperator.IsComparison)
        {
            if (!IsComparable(left.Type, right.Type))
                _diagnostics.ReportTypeMismatch(binary.Span, left.Type.Name, right.Type.Name);

            if (left.Type.IsInteger && right.Type.IsInteger)
            {
                TypeSymbol numericType = BestNumericType(left.Type, right.Type);
                left = BindConversion(left, numericType, left.Span, reportMismatch: false);
                right = BindConversion(right, numericType, right.Span, reportMismatch: false);
            }

            return new BoundBinaryExpression(left, binaryOperator, right, binary.Span, BuiltinTypes.Bool);
        }

        if (binaryOperator.Kind is BoundBinaryOperatorKind.LogicalAnd or BoundBinaryOperatorKind.LogicalOr)
        {
            if (!left.Type.IsBool || !right.Type.IsBool)
                _diagnostics.ReportTypeMismatch(binary.Span, "bool", $"{left.Type.Name}, {right.Type.Name}");
            left = BindConversion(left, BuiltinTypes.Bool, left.Span, reportMismatch: false);
            right = BindConversion(right, BuiltinTypes.Bool, right.Span, reportMismatch: false);
            return new BoundBinaryExpression(left, binaryOperator, right, binary.Span, BuiltinTypes.Bool);
        }

        if (!left.Type.IsInteger || !right.Type.IsInteger)
            _diagnostics.ReportTypeMismatch(binary.Span, "integer", $"{left.Type.Name}, {right.Type.Name}");

        TypeSymbol resultType = BestNumericType(left.Type, right.Type);
        left = BindConversion(left, resultType, left.Span, reportMismatch: false);
        right = BindConversion(right, resultType, right.Span, reportMismatch: false);
        return new BoundBinaryExpression(left, binaryOperator, right, binary.Span, resultType);
    }

    private BoundExpression BindMemberAccessExpression(MemberAccessExpressionSyntax memberAccess)
    {
        if (memberAccess.Expression is NameExpressionSyntax nameExpression
            && !_currentScope.TryLookup(nameExpression.Name.Text, out _)
            && TryResolveEnumTypeAlias(nameExpression.Name.Text, nameExpression.Name.Span, out EnumTypeSymbol? qualifiedEnum)
            && qualifiedEnum is not null)
        {
            return BindQualifiedEnumMember(memberAccess, qualifiedEnum);
        }

        BoundExpression receiver = BindExpression(memberAccess.Expression);
        if (TryGetAggregateMember(receiver.Type, memberAccess.Member.Text, out AggregateMemberSymbol? member)
            && member is not null)
        {
            return new BoundMemberAccessExpression(receiver, member, memberAccess.Span);
        }

        if (receiver.Type is ModuleTypeSymbol moduleType)
        {
            ImportedModule module = moduleType.Module.Module;
            if (module.ExportedFunctions.TryGetValue(memberAccess.Member.Text, out FunctionSymbol? function))
                return new BoundSymbolExpression(function, memberAccess.Span, new FunctionTypeSymbol(function));

            if (module.ExportedVariables.TryGetValue(memberAccess.Member.Text, out VariableSymbol? variable))
                return new BoundSymbolExpression(variable, memberAccess.Span, variable.Type);

            if (module.ImportedModules.TryGetValue(memberAccess.Member.Text, out ImportedModule? importedModule))
                return new BoundSymbolExpression(new ModuleSymbol(memberAccess.Member.Text, importedModule), memberAccess.Span, new ModuleTypeSymbol(new ModuleSymbol(memberAccess.Member.Text, importedModule)));

            _diagnostics.ReportUndefinedName(memberAccess.Member.Span, memberAccess.Member.Text);
            return new BoundErrorExpression(memberAccess.Span);
        }

        if (receiver.Type is StructTypeSymbol or UnionTypeSymbol or BitfieldTypeSymbol)
            _diagnostics.ReportUndefinedName(memberAccess.Member.Span, memberAccess.Member.Text);

        return new BoundMemberAccessExpression(
            receiver,
            new AggregateMemberSymbol(memberAccess.Member.Text, BuiltinTypes.Unknown, byteOffset: 0, bitOffset: 0, bitWidth: 0, isBitfield: false),
            memberAccess.Span);
    }

    private BoundExpression BindPointerDerefExpression(PointerDerefExpressionSyntax pointerDeref)
    {
        BoundExpression expression = BindExpression(pointerDeref.Expression);
        TypeSymbol type = BindPointerDerefType(pointerDeref.Expression.Span, expression.Type);
        return new BoundPointerDerefExpression(expression, pointerDeref.Span, type);
    }

    private TypeSymbol BindPointerDerefType(TextSpan span, TypeSymbol expressionType)
    {
        if (expressionType is PointerTypeSymbol pointerType)
            return pointerType.PointeeType;

        _diagnostics.ReportTypeMismatch(span, "pointer", expressionType.Name);
        return BuiltinTypes.Unknown;
    }

    private BoundExpression BindIndexExpression(IndexExpressionSyntax indexExpression)
    {
        BoundExpression expression = BindExpression(indexExpression.Expression);
        BoundExpression index = BindExpression(indexExpression.Index);
        if (!index.Type.IsInteger)
            _diagnostics.ReportTypeMismatch(indexExpression.Index.Span, "integer", index.Type.Name);

        TypeSymbol type = expression.Type switch
        {
            ArrayTypeSymbol array => array.ElementType,
            MultiPointerTypeSymbol pointer => pointer.PointeeType,
            _ => BuiltinTypes.Unknown,
        };

        if (expression.Type is PointerTypeSymbol)
            _diagnostics.ReportTypeMismatch(indexExpression.Expression.Span, "array or [*]pointer", expression.Type.Name);

        return new BoundIndexExpression(expression, index, indexExpression.Span, type);
    }

    private BoundExpression BindEnumLiteralExpression(EnumLiteralExpressionSyntax enumLiteral, TypeSymbol? expectedType)
    {
        if (expectedType is not EnumTypeSymbol enumType)
        {
            _diagnostics.ReportEnumLiteralRequiresContext(enumLiteral.Span, enumLiteral.MemberName.Text);
            return new BoundErrorExpression(enumLiteral.Span);
        }

        if (!enumType.Members.TryGetValue(enumLiteral.MemberName.Text, out long value))
        {
            _diagnostics.ReportUndefinedName(enumLiteral.MemberName.Span, enumLiteral.MemberName.Text);
            return new BoundErrorExpression(enumLiteral.Span);
        }

        return new BoundEnumLiteralExpression(enumType, enumLiteral.MemberName.Text, value, enumLiteral.Span);
    }

    private BoundExpression BindQualifiedEnumMember(MemberAccessExpressionSyntax memberAccess, EnumTypeSymbol enumType)
    {
        if (!enumType.Members.TryGetValue(memberAccess.Member.Text, out long value))
        {
            _diagnostics.ReportUndefinedName(memberAccess.Member.Span, memberAccess.Member.Text);
            return new BoundErrorExpression(memberAccess.Span);
        }

        return new BoundEnumLiteralExpression(enumType, memberAccess.Member.Text, value, memberAccess.Span);
    }

    private bool TryResolveEnumTypeAlias(string aliasName, TextSpan span, out EnumTypeSymbol? enumType)
    {
        if (!_resolvedTypeAliases.ContainsKey(aliasName) && !_typeAliases.ContainsKey(aliasName))
        {
            enumType = null;
            return false;
        }

        TypeSymbol resolved = ResolveTypeAlias(aliasName, span);
        enumType = resolved as EnumTypeSymbol;
        return enumType is not null;
    }

    private static bool TryGetAggregateMember(TypeSymbol type, string memberName, out AggregateMemberSymbol? member)
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
        if (callee.Type is ModuleTypeSymbol moduleType)
        {
            if (callExpression.Arguments.Count != 0)
                _diagnostics.ReportArgumentCountMismatch(callExpression.Span, moduleType.Module.Name, 0, callExpression.Arguments.Count);
            _ = BindArgumentsLoose(callExpression.Arguments);
            return new BoundModuleCallExpression(moduleType.Module.Module, callExpression.Span);
        }

        if (!TryGetFunctionSymbol(callee, out FunctionSymbol? maybeFunction) || maybeFunction is null)
        {
            _diagnostics.ReportNotCallable(callExpression.Callee.Span, callee.Type.Name);
            _ = BindArgumentsLoose(callExpression.Arguments);
            return new BoundErrorExpression(callExpression.Span);
        }

        FunctionSymbol function = maybeFunction;
        List<BoundExpression> arguments = BindCallArguments(function, callExpression.Arguments, callExpression.Callee.Span);
        TypeSymbol returnType = function.ReturnTypes.Count switch
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

    private BoundExpression RequireComptimeExpression(BoundExpression expression, TextSpan span)
    {
        if (expression is BoundErrorExpression)
            return expression;

        if (TryEvaluateFoldedValue(expression, out object? value, out ComptimeFailure foldFailure))
            return new BoundLiteralExpression(value, expression.Span, expression.Type);

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
        if (TryEvaluateFoldedValue(expression, out object? value, out ComptimeFailure failure))
        {
            folded = new BoundLiteralExpression(value, expression.Span, expression.Type);
            return true;
        }

        if (reportDiagnostics)
            ReportComptimeFailure(failure);

        folded = expression;
        return false;
    }

    private bool TryEvaluateFoldedValue(BoundExpression expression, out object? value, out ComptimeFailure failure)
    {
        ComptimeEvaluator evaluator = new(
            _comptimeFuel,
            ResolveFunctionBodyForComptime,
            GetComptimeSupportResult);
        return evaluator.TryEvaluateExpression(expression, out value, out failure);
    }

    private bool TryEvaluateAssertCondition(BoundExpression expression, out object? value, out ComptimeFailure failure)
    {
        ComptimeEvaluator evaluator = new(
            _comptimeFuel,
            ResolveFunctionBodyForComptime,
            GetComptimeSupportResult);
        return evaluator.TryEvaluateExpression(expression, _knownConstantValues, out value, out failure);
    }

    private void RememberKnownConstantValue(VariableSymbol symbol, BoundExpression? initializer)
    {
        if (!symbol.IsConst || initializer is null || initializer is BoundErrorExpression)
            return;

        if (TryEvaluateConstantValue(initializer, out object? value))
            _knownConstantValues[symbol] = value;
    }

    private static bool TryValidateComptimeExpression(BoundExpression expression, out ComptimeFailure failure)
    {
        switch (expression)
        {
            case BoundLiteralExpression:
            case BoundEnumLiteralExpression:
                failure = ComptimeFailure.None;
                return true;

            case BoundUnaryExpression unary:
                if (unary.Operator.Kind is BoundUnaryOperatorKind.AddressOf or BoundUnaryOperatorKind.PostIncrement or BoundUnaryOperatorKind.PostDecrement)
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

                failure = ComptimeFailure.None;
                return true;

            case BoundStructLiteralExpression structLiteral:
                foreach (BoundStructFieldInitializer field in structLiteral.Fields)
                {
                    if (!TryValidateComptimeExpression(field.Value, out failure))
                        return false;
                }

                failure = ComptimeFailure.None;
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

            case BoundModuleCallExpression moduleCall:
                failure = new ComptimeFailure(ComptimeFailureKind.UnsupportedConstruct, moduleCall.Span, "module calls are not supported in comptime-required contexts.");
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
            BoundLiteralExpression literal => literal.Value is not string,
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

    private BoundBlockStatement? ResolveFunctionBodyForComptime(FunctionSymbol function)
    {
        if (_boundFunctionBodies.TryGetValue(function, out BoundBlockStatement? localBody))
            return localBody;

        foreach (ImportedModule module in _importedModules.Values)
        {
            if (TryResolveImportedFunctionBody(module, function, out BoundBlockStatement? importedBody))
                return importedBody;
        }

        return null;
    }

    private static bool TryResolveImportedFunctionBody(ImportedModule module, FunctionSymbol function, out BoundBlockStatement? body)
    {
        foreach (BoundFunctionMember member in module.Program.Functions)
        {
            if (ReferenceEquals(member.Symbol, function))
            {
                body = member.Body;
                return true;
            }
        }

        foreach (ImportedModule nestedModule in module.ImportedModules.Values)
        {
            if (TryResolveImportedFunctionBody(nestedModule, function, out body))
                return true;
        }

        body = null;
        return false;
    }

    private ComptimeSupportResult GetComptimeSupportResult(FunctionSymbol function)
    {
        if (_comptimeSupportCache.TryGetValue(function, out ComptimeSupportResult cached))
            return cached;

        BoundBlockStatement? body = ResolveFunctionBodyForComptime(function);
        Debug.Assert(body is not null, "Comptime support analysis should only run after body resolution succeeds.");

        ComptimeFunctionSupportAnalyzer analyzer = new();
        ComptimeSupportResult analyzed = analyzer.Analyze(function, Requires.NotNull(body));
        _comptimeSupportCache[function] = analyzed;
        return analyzed;
    }

    private void ReportComptimeFailure(ComptimeFailure failure)
    {
        Debug.Assert(failure.Kind != ComptimeFailureKind.None, "ReportComptimeFailure should only be called with an actual failure.");

        switch (failure.Kind)
        {
            case ComptimeFailureKind.NotEvaluable:
                _diagnostics.ReportComptimeValueRequired(failure.Span);
                break;

            case ComptimeFailureKind.UnsupportedConstruct:
                _diagnostics.ReportComptimeUnsupportedConstruct(failure.Span, failure.Detail);
                break;

            case ComptimeFailureKind.ForbiddenSymbolAccess:
                _diagnostics.ReportComptimeForbiddenSymbolAccess(failure.Span, failure.Detail);
                break;

            case ComptimeFailureKind.FuelExhausted:
                _diagnostics.ReportComptimeFuelExhausted(failure.Span);
                break;
        }
    }

    private BoundExpression BindIntrinsicCallExpression(IntrinsicCallExpressionSyntax intrinsic)
    {
        if (!P2InstructionMetadata.IsValidInstruction(intrinsic.Name.Text))
        {
            _diagnostics.ReportUnknownBuiltin(intrinsic.Name.Span, intrinsic.Name.Text);
            return new BoundErrorExpression(intrinsic.Span);
        }

        List<BoundExpression> arguments = new(intrinsic.Arguments.Count);
        foreach (ExpressionSyntax argument in intrinsic.Arguments)
            arguments.Add(BindExpression(argument));

        return new BoundIntrinsicCallExpression(intrinsic.Name.Text, arguments, intrinsic.Span, BuiltinTypes.U32);
    }

    private BoundExpression BindStructLiteralExpression(StructLiteralExpressionSyntax structLiteral, TypeSymbol? expectedType)
    {
        if (expectedType is StructTypeSymbol structType)
        {
            List<BoundStructFieldInitializer> initializers = new(structLiteral.Initializers.Count);
            foreach (FieldInitializerSyntax initializer in structLiteral.Initializers)
            {
                if (!structType.Fields.TryGetValue(initializer.Name.Text, out TypeSymbol? fieldType))
                {
                    _diagnostics.ReportUndefinedName(initializer.Name.Span, initializer.Name.Text);
                    initializers.Add(new BoundStructFieldInitializer(initializer.Name.Text, BindExpression(initializer.Value)));
                    continue;
                }

                BoundExpression value = BindExpression(initializer.Value, fieldType);
                initializers.Add(new BoundStructFieldInitializer(initializer.Name.Text, value));
            }

            return new BoundStructLiteralExpression(initializers, structLiteral.Span, structType);
        }

        Dictionary<string, TypeSymbol> fields = new(StringComparer.Ordinal);
        List<BoundStructFieldInitializer> inferredInitializers = new(structLiteral.Initializers.Count);
        foreach (FieldInitializerSyntax initializer in structLiteral.Initializers)
        {
            BoundExpression value = BindExpression(initializer.Value);
            if (!fields.TryAdd(initializer.Name.Text, value.Type))
                _diagnostics.ReportSymbolAlreadyDeclared(initializer.Name.Span, initializer.Name.Text);
            inferredInitializers.Add(new BoundStructFieldInitializer(initializer.Name.Text, value));
        }

        _anonymousStructIndex++;
        StructTypeSymbol inferredType = new($"<struct#{_anonymousStructIndex}>", fields);
        return new BoundStructLiteralExpression(inferredInitializers, structLiteral.Span, inferredType);
    }

    private BoundExpression BindTypedStructLiteralExpression(TypedStructLiteralExpressionSyntax syntax)
    {
        // The parser guarantees TypeName is a NameExpressionSyntax.
        NameExpressionSyntax nameExpr = (NameExpressionSyntax)syntax.TypeName;
        TypeSymbol resolvedType = ResolveTypeAlias(nameExpr.Name.Text, nameExpr.Name.Span);
        if (resolvedType is not StructTypeSymbol structType)
        {
            if (!resolvedType.IsUnknown)
                _diagnostics.ReportTypeMismatch(nameExpr.Name.Span, "struct", resolvedType.Name);
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
                _diagnostics.ReportStructDuplicateField(initializer.Name.Span, fieldName);
                BindExpression(initializer.Value);
                continue;
            }

            if (!structType.Fields.TryGetValue(fieldName, out TypeSymbol? fieldType))
            {
                _diagnostics.ReportStructUnknownField(initializer.Name.Span, structType.Name, fieldName);
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
            _diagnostics.ReportStructMissingFields(syntax.CloseBrace.Span, structType.Name, string.Join(", ", missing));
        }

        return new BoundStructLiteralExpression(initializers, syntax.Span, structType);
    }

    private BoundExpression BindArrayLiteralExpression(ArrayLiteralExpressionSyntax arrayLiteral, TypeSymbol? expectedType)
    {
        ArrayTypeSymbol? expectedArrayType = expectedType as ArrayTypeSymbol;
        int? expectedLength = expectedArrayType?.Length;
        TypeSymbol? elementType = expectedArrayType?.ElementType;

        bool lastElementIsSpread = false;
        for (int i = 0; i < arrayLiteral.Elements.Count; i++)
        {
            ArrayElementSyntax element = arrayLiteral.Elements[i];
            if (element.Spread is null)
                continue;

            Token spreadToken = element.Spread.Value;
            if (i != arrayLiteral.Elements.Count - 1)
                _diagnostics.ReportArrayLiteralSpreadMustBeLast(spreadToken.Span);
            else
                lastElementIsSpread = true;
        }

        if ((arrayLiteral.Elements.Count == 0 || lastElementIsSpread) && expectedLength is null)
        {
            _diagnostics.ReportArrayLiteralRequiresContext(arrayLiteral.Span);
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

        TypeSymbol resolvedElementType = Requires.NotNull(elementType);
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

        _diagnostics.ReportTypeMismatch(ifExpression.Span, thenExpression.Type.Name, elseExpression.Type.Name);
        return new BoundIfExpression(condition, thenExpression, elseExpression, ifExpression.Span, BuiltinTypes.Unknown);
    }

    private BoundExpression BindRangeExpression(RangeExpressionSyntax rangeExpression)
    {
        BoundExpression start = BindExpression(rangeExpression.Start);
        BoundExpression end = BindExpression(rangeExpression.End);

        if (!start.Type.IsInteger)
            _diagnostics.ReportTypeMismatch(rangeExpression.Start.Span, "integer", start.Type.Name);
        if (!end.Type.IsInteger)
            _diagnostics.ReportTypeMismatch(rangeExpression.End.Span, "integer", end.Type.Name);

        return new BoundRangeExpression(start, end, rangeExpression.Span);
    }

    private BoundExpression BindCastExpression(CastExpressionSyntax castExpression)
    {
        BoundExpression expression = BindExpression(castExpression.Expression);
        TypeSymbol targetType = BindType(castExpression.TargetType);

        if (!CanExplicitlyCast(expression.Type, targetType))
        {
            _diagnostics.ReportInvalidExplicitCast(castExpression.Span, expression.Type.Name, targetType.Name);
            return new BoundErrorExpression(castExpression.Span);
        }

        return new BoundCastExpression(expression, castExpression.Span, targetType);
    }

    private BoundExpression BindBitcastExpression(BitcastExpressionSyntax bitcastExpression)
    {
        BoundExpression expression = BindExpression(bitcastExpression.Value);
        TypeSymbol targetType = BindType(bitcastExpression.TargetType);

        if (!TypeFacts.IsScalarCastType(expression.Type) || !TypeFacts.IsScalarCastType(targetType))
        {
            _diagnostics.ReportInvalidExplicitCast(bitcastExpression.Span, expression.Type.Name, targetType.Name);
            return new BoundErrorExpression(bitcastExpression.Span);
        }

        bool gotSourceWidth = TypeFacts.TryGetScalarWidth(expression.Type, out int sourceWidth);
        bool gotTargetWidth = TypeFacts.TryGetScalarWidth(targetType, out int targetWidth);
        Debug.Assert(gotSourceWidth && gotTargetWidth, "Scalar cast types must always report a scalar width.");

        if (sourceWidth != targetWidth)
        {
            _diagnostics.ReportBitcastSizeMismatch(bitcastExpression.Span, expression.Type.Name, targetType.Name);
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
            _diagnostics.ReportMemoryofRequiresVariable(query.Span);
            return new BoundErrorExpression(query.Span);
        }

        TypeSymbol type = BindType(query.Subject);
        BoundExpression memorySpaceExpr = BindExpression(query.MemorySpace!, expectedType: MemorySpaceType);

        VariableStorageClass? storageClass = TryResolveMemorySpace(memorySpaceExpr, query.MemorySpace!.Span);
        if (storageClass is null)
            return new BoundErrorExpression(query.Span);

        if (query.Keyword.Kind == TokenKind.SizeofKeyword)
        {
            if (!TypeFacts.TryGetSizeInMemorySpace(type, storageClass.Value, out int size))
            {
                _diagnostics.ReportQueryUnsupportedType(query.Subject.Span, operatorName, type.Name);
                return new BoundErrorExpression(query.Span);
            }

            return new BoundLiteralExpression((long)size, query.Span, BuiltinTypes.IntegerLiteral);
        }

        Debug.Assert(query.Keyword.Kind == TokenKind.AlignofKeyword, "Two-arg query must be sizeof or alignof.");

        if (!TypeFacts.TryGetAlignmentInMemorySpace(type, storageClass.Value, out int alignment))
        {
            _diagnostics.ReportQueryUnsupportedType(query.Subject.Span, operatorName, type.Name);
            return new BoundErrorExpression(query.Span);
        }

        return new BoundLiteralExpression((long)alignment, query.Span, BuiltinTypes.IntegerLiteral);
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
                _diagnostics.ReportMemoryofRequiresVariable(query.Subject.Span);
                return new BoundErrorExpression(query.Span);
            }

            _diagnostics.ReportQueryRequiresMemorySpace(query.Subject.Span, operatorName);
            return new BoundErrorExpression(query.Span);
        }

        string name = namedType.Name.Text;

        if (!_currentScope.TryLookup(name, out Symbol? symbol) || symbol is null)
        {
            _diagnostics.ReportUndefinedName(namedType.Name.Span, name);
            return new BoundErrorExpression(query.Span);
        }

        if (symbol is VariableSymbol variable)
        {
            if (variable.IsAutomatic)
            {
                _diagnostics.ReportQueryAutomaticLocal(query.Subject.Span, operatorName, name);
                return new BoundErrorExpression(query.Span);
            }

            return FoldVariableQuery(query, variable, operatorName);
        }

        if (symbol is ParameterSymbol)
        {
            _diagnostics.ReportQueryAutomaticLocal(query.Subject.Span, operatorName, name);
            return new BoundErrorExpression(query.Span);
        }

        // Symbol is not a variable (could be a function, module, or type alias).
        if (query.Keyword.Kind == TokenKind.MemoryofKeyword)
        {
            _diagnostics.ReportMemoryofRequiresVariable(query.Subject.Span);
            return new BoundErrorExpression(query.Span);
        }

        _diagnostics.ReportQueryRequiresMemorySpace(query.Subject.Span, operatorName);
        return new BoundErrorExpression(query.Span);
    }

    private BoundExpression FoldVariableQuery(QueryExpressionSyntax query, VariableSymbol variable, string operatorName)
    {
        VariableStorageClass sc = variable.StorageClass;

        if (query.Keyword.Kind == TokenKind.MemoryofKeyword)
        {
            (string memberName, long value) = sc switch
            {
                VariableStorageClass.Reg => ("reg", 0L),
                VariableStorageClass.Lut => ("lut", 1L),
                VariableStorageClass.Hub => ("hub", 2L),
                _ => throw new UnreachableException(),
            };

            return new BoundEnumLiteralExpression(MemorySpaceType, memberName, value, query.Span);
        }

        if (query.Keyword.Kind == TokenKind.SizeofKeyword)
        {
            if (!TypeFacts.TryGetSizeInMemorySpace(variable.Type, sc, out int size))
            {
                _diagnostics.ReportQueryUnsupportedType(query.Subject.Span, operatorName, variable.Type.Name);
                return new BoundErrorExpression(query.Span);
            }

            return new BoundLiteralExpression((long)size, query.Span, BuiltinTypes.IntegerLiteral);
        }

        Debug.Assert(query.Keyword.Kind == TokenKind.AlignofKeyword, "Variable query must be sizeof, alignof, or memoryof.");

        if (!TypeFacts.TryGetAlignmentInMemorySpace(variable.Type, sc, out int alignment))
        {
            _diagnostics.ReportQueryUnsupportedType(query.Subject.Span, operatorName, variable.Type.Name);
            return new BoundErrorExpression(query.Span);
        }

        return new BoundLiteralExpression((long)alignment, query.Span, BuiltinTypes.IntegerLiteral);
    }

    private VariableStorageClass? TryResolveMemorySpace(BoundExpression expression, TextSpan span)
    {
        if (expression is BoundEnumLiteralExpression enumLiteral)
        {
            return enumLiteral.Value switch
            {
                0 => VariableStorageClass.Reg,
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
                0 => VariableStorageClass.Reg,
                1 => VariableStorageClass.Lut,
                2 => VariableStorageClass.Hub,
                _ => ReportInvalidMemorySpace(span),
            };
        }

        _diagnostics.ReportInvalidMemorySpaceArgument(span);
        return null;
    }

    private VariableStorageClass? ReportInvalidMemorySpace(TextSpan span)
    {
        _diagnostics.ReportInvalidMemorySpaceArgument(span);
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
                _diagnostics.ReportArgumentCountMismatch(callSiteSpan, function.Name, function.Parameters.Count, arguments.Count);
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
                    _diagnostics.ReportUnknownNamedArgument(namedArgument.Name.Span, function.Name, namedArgument.Name.Text);
                    _ = BindExpression(namedArgument.Value);
                    continue;
                }

                TypeSymbol parameterType = function.Parameters[parameterIndex].Type;
                BoundExpression boundValue = BindExpression(namedArgument.Value, parameterType);
                if (filled[parameterIndex])
                {
                    if (filledByNamed[parameterIndex])
                    {
                        _diagnostics.ReportDuplicateNamedArgument(namedArgument.Name.Span, namedArgument.Name.Text);
                    }
                    else
                    {
                        _diagnostics.ReportNamedArgumentConflictsWithPositional(namedArgument.Name.Span, namedArgument.Name.Text);
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
                _diagnostics.ReportPositionalArgumentAfterNamed(argument.Span, function.Name);
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
            _diagnostics.ReportArgumentCountMismatch(callSiteSpan, function.Name, function.Parameters.Count, filledCount);
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

    private BoundExpression BindConversion(BoundExpression expression, TypeSymbol targetType, TextSpan span, bool reportMismatch)
    {
        if (targetType.IsUnknown || expression.Type.IsUnknown)
            return expression;

        if (targetType.Name == expression.Type.Name)
            return expression;

        // String literal → [*]<storage> u8 (non-const) is rejected with a dedicated diagnostic
        if (ReferenceEquals(expression.Type, BuiltinTypes.String)
            && targetType is MultiPointerTypeSymbol { PointeeType: var pointeeType, IsConst: false }
            && ReferenceEquals(pointeeType, BuiltinTypes.U8))
        {
            _diagnostics.ReportStringToNonConstPointer(span);
            return new BoundErrorExpression(span);
        }

        if (!IsAssignable(targetType, expression.Type))
        {
            if (reportMismatch)
                _diagnostics.ReportTypeMismatch(span, targetType.Name, expression.Type.Name);
            return new BoundErrorExpression(span);
        }

        // String literal → [N]u8: synthesize an array literal from the string bytes
        if (ReferenceEquals(expression.Type, BuiltinTypes.String)
            && targetType is ArrayTypeSymbol targetArray
            && ReferenceEquals(targetArray.ElementType, BuiltinTypes.U8)
            && targetArray.Length is int targetLength
            && expression is BoundLiteralExpression { Value: string stringValue })
        {
            return LowerStringToArrayLiteral(stringValue, targetArray, targetLength, span, reportMismatch);
        }

        return new BoundConversionExpression(expression, span, targetType);
    }

    private BoundExpression LowerStringToArrayLiteral(
        string stringValue,
        ArrayTypeSymbol targetArray, int targetLength,
        TextSpan span, bool reportMismatch)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(stringValue);

        if (bytes.Length != targetLength)
        {
            if (reportMismatch)
                _diagnostics.ReportStringLengthMismatch(span, targetLength, bytes.Length);
            return new BoundErrorExpression(span);
        }

        List<BoundExpression> elements = new(targetLength);
        foreach (byte b in bytes)
            elements.Add(new BoundLiteralExpression((long)b, span, BuiltinTypes.IntegerLiteral));

        return new BoundArrayLiteralExpression(elements, lastElementIsSpread: false, span, targetArray);
    }

    private static bool CanExplicitlyCast(TypeSymbol sourceType, TypeSymbol targetType)
    {
        if (sourceType.IsUnknown || targetType.IsUnknown)
            return true;

        if (ReferenceEquals(sourceType, targetType))
            return true;

        if (sourceType is EnumTypeSymbol sourceEnum && targetType is EnumTypeSymbol targetEnum)
            return ReferenceEquals(sourceEnum, targetEnum);

        if (sourceType is EnumTypeSymbol openEnumSource
            && openEnumSource.IsOpen
            && targetType.IsInteger
            && openEnumSource.BackingType.Name == targetType.Name)
        {
            return true;
        }

        if (targetType is EnumTypeSymbol openEnumTarget
            && openEnumTarget.IsOpen
            && sourceType.IsInteger
            && openEnumTarget.BackingType.Name == sourceType.Name)
        {
            return true;
        }

        if (sourceType.IsInteger
            && targetType.IsInteger
            && TypeFacts.TryGetIntegerWidth(sourceType, out _)
            && TypeFacts.TryGetIntegerWidth(targetType, out _))
        {
            return true;
        }

        return sourceType is PointerLikeTypeSymbol && targetType is PointerLikeTypeSymbol;
    }

    private VariableSymbol CreateVariableSymbol(
        VariableDeclarationSyntax declaration,
        TypeSymbol variableType,
        VariableScopeKind scopeKind)
    {
        VariableStorageClass storageClass = MapStorageClass(declaration.StorageClassKeyword);
        bool isConst = declaration.MutabilityKeyword.Kind == TokenKind.ConstKeyword;
        bool isGlobalStorage = scopeKind == VariableScopeKind.GlobalStorage;

        if (!isGlobalStorage)
        {
            if (declaration.ExternKeyword is Token externKeyword)
                _diagnostics.ReportInvalidExternScope(externKeyword.Span);

            if (declaration.StorageClassKeyword is Token storageClassKeyword)
                _diagnostics.ReportInvalidLocalStorageClass(storageClassKeyword.Span, storageClassKeyword.Text);
        }

        return new VariableSymbol(
            declaration.Name.Text,
            variableType,
            isConst,
            storageClass,
            scopeKind,
            declaration.ExternKeyword is not null,
            fixedAddress: null,
            alignment: null);
    }

    private static VariableStorageClass MapStorageClass(Token? storageClassKeyword)
    {
        return storageClassKeyword?.Kind switch
        {
            TokenKind.RegKeyword => VariableStorageClass.Reg,
            TokenKind.LutKeyword => VariableStorageClass.Lut,
            TokenKind.HubKeyword => VariableStorageClass.Hub,
            _ => VariableStorageClass.Automatic,
        };
    }

    private void ResolveLayoutMetadata(VariableDeclarationSyntax declaration, VariableSymbol variableSymbol)
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
            _diagnostics.ReportTypeMismatch(span, "comptime integer", bound.Type.Name);
        return value;
    }

    private int? TryEvaluateConstantInt(ExpressionSyntax? expression)
    {
        if (expression is null)
            return null;

        BoundExpression bound = BindExpression(expression);
        return TryEvaluateConstantInt(bound);
    }

    private bool TryEvaluateConstantValue(BoundExpression expression, out object? value)
    {
        return TryEvaluateFoldedValue(expression, out value, out _);
    }

    private int? TryEvaluateConstantInt(BoundExpression expression)
    {
        if (!TryEvaluateConstantValue(expression, out object? value))
            return null;

        return value switch
        {
            int or uint or long or ulong or short or ushort or byte or sbyte => ToInt32Unchecked(value),
            _ => null,
        };
    }

    private static int ToInt32Unchecked(object? value)
    {
        return value switch
        {
            null => 0,
            int i => i,
            uint u => unchecked((int)u),
            long l => unchecked((int)l),
            ulong u => unchecked((int)u),
            short s => s,
            ushort u => u,
            byte b => b,
            sbyte s => s,
            _ => unchecked((int)Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
        };
    }

    private static bool RequiresComptimeInitializer(VariableSymbol symbol)
        => symbol.IsGlobalStorage && symbol.StorageClass != VariableStorageClass.Automatic;

    private TypeSymbol BindType(TypeSyntax syntax, string? aliasName = null)
    {
        return syntax switch
        {
            PrimitiveTypeSyntax primitive => BindPrimitiveType(primitive.Keyword),
            GenericWidthTypeSyntax generic => BindGenericWidthType(generic),
            ArrayTypeSyntax array => BindArrayType(array),
            PointerTypeSyntax pointer => new PointerTypeSymbol(
                BindType(pointer.PointeeType),
                pointer.ConstKeyword is not null,
                pointer.VolatileKeyword is not null,
                TryEvaluateConstantInt(pointer.AlignClause?.Alignment),
                MapStorageClass(pointer.StorageClassKeyword)),
            MultiPointerTypeSyntax multiPointer => new MultiPointerTypeSymbol(
                BindType(multiPointer.PointeeType),
                multiPointer.ConstKeyword is not null,
                multiPointer.VolatileKeyword is not null,
                TryEvaluateConstantInt(multiPointer.AlignClause?.Alignment),
                MapStorageClass(multiPointer.StorageClassKeyword)),
            StructTypeSyntax structType => BindStructType(structType, aliasName),
            UnionTypeSyntax unionType => BindUnionType(unionType, aliasName),
            EnumTypeSyntax enumType => BindEnumType(enumType, aliasName),
            BitfieldTypeSyntax bitfieldType => BindBitfieldType(bitfieldType, aliasName),
            NamedTypeSyntax named => BindNamedType(named),
            QualifiedTypeSyntax qualified => BindQualifiedType(qualified),
            _ => BuiltinTypes.Unknown, // blade:no-codecov
        };
    }

    private TypeSymbol BindPrimitiveType(Token keywordToken)
    {
        if (BuiltinTypes.TryGet(keywordToken.Text, out TypeSymbol type))
            return type;

        _diagnostics.ReportUndefinedType(keywordToken.Span, keywordToken.Text);
        return BuiltinTypes.Unknown;
    }

    private TypeSymbol BindGenericWidthType(GenericWidthTypeSyntax genericType)
    {
        BoundExpression width = BindExpression(genericType.Width);
        if (!width.Type.IsInteger)
            _diagnostics.ReportTypeMismatch(genericType.Width.Span, "integer", width.Type.Name);

        return genericType.Keyword.Kind == TokenKind.UintKeyword ? BuiltinTypes.Uint : BuiltinTypes.Int;
    }

    private TypeSymbol BindArrayType(ArrayTypeSyntax arrayType)
    {
        BoundExpression size = BindExpression(arrayType.Size);
        if (!size.Type.IsInteger)
            _diagnostics.ReportTypeMismatch(arrayType.Size.Span, "integer", size.Type.Name);

        TypeSymbol elementType = BindType(arrayType.ElementType);
        return new ArrayTypeSymbol(elementType, TryEvaluateConstantInt(size));
    }

    private TypeSymbol BindStructType(StructTypeSyntax structType, string? aliasName)
    {
        Dictionary<string, TypeSymbol> fields = new(StringComparer.Ordinal);
        Dictionary<string, AggregateMemberSymbol> members = new(StringComparer.Ordinal);
        int nextOffset = 0;
        int maxAlignment = 1;
        foreach (StructFieldSyntax field in structType.Fields)
        {
            TypeSymbol fieldType = BindType(field.Type);
            if (!fields.TryAdd(field.Name.Text, fieldType))
            {
                _diagnostics.ReportSymbolAlreadyDeclared(field.Name.Span, field.Name.Text);
                continue;
            }

            int fieldSize = TypeFacts.TryGetSizeBytes(fieldType, out int computedFieldSize) ? computedFieldSize : 0;
            int fieldAlignment = TypeFacts.TryGetAlignmentBytes(fieldType, out int computedFieldAlignment) ? computedFieldAlignment : 1;
            nextOffset = AlignTo(nextOffset, fieldAlignment);

            members[field.Name.Text] = new AggregateMemberSymbol(field.Name.Text, fieldType, nextOffset, bitOffset: 0, bitWidth: 0, isBitfield: false);
            nextOffset += fieldSize;
            maxAlignment = Math.Max(maxAlignment, fieldAlignment);
        }

        string name = aliasName ?? $"<anon-struct#{++_anonymousStructIndex}>";
        int sizeBytes = AlignTo(nextOffset, maxAlignment);
        return new StructTypeSymbol(name, fields, members, sizeBytes, maxAlignment);
    }

    private TypeSymbol BindUnionType(UnionTypeSyntax unionType, string? aliasName)
    {
        Dictionary<string, TypeSymbol> fields = new(StringComparer.Ordinal);
        Dictionary<string, AggregateMemberSymbol> members = new(StringComparer.Ordinal);
        int maxSize = 0;
        int maxAlignment = 1;
        foreach (StructFieldSyntax field in unionType.Fields)
        {
            TypeSymbol fieldType = BindType(field.Type);
            if (!fields.TryAdd(field.Name.Text, fieldType))
            {
                _diagnostics.ReportSymbolAlreadyDeclared(field.Name.Span, field.Name.Text);
                continue;
            }

            int fieldSize = TypeFacts.TryGetSizeBytes(fieldType, out int computedFieldSize) ? computedFieldSize : 0;
            int fieldAlignment = TypeFacts.TryGetAlignmentBytes(fieldType, out int computedFieldAlignment) ? computedFieldAlignment : 1;
            members[field.Name.Text] = new AggregateMemberSymbol(field.Name.Text, fieldType, byteOffset: 0, bitOffset: 0, bitWidth: 0, isBitfield: false);
            maxSize = Math.Max(maxSize, fieldSize);
            maxAlignment = Math.Max(maxAlignment, fieldAlignment);
        }

        string name = aliasName ?? $"<anon-union#{++_anonymousStructIndex}>";
        return new UnionTypeSymbol(name, fields, members, maxSize, maxAlignment);
    }

    private TypeSymbol BindEnumType(EnumTypeSyntax enumTypeSyntax, string? aliasName)
    {
        TypeSymbol backingType = BindType(enumTypeSyntax.BackingType);
        if (!backingType.IsInteger)
            _diagnostics.ReportTypeMismatch(enumTypeSyntax.BackingType.Span, "integer", backingType.Name);

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
                _diagnostics.ReportSymbolAlreadyDeclared(member.Name.Span, member.Name.Text);
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
                    _diagnostics.ReportTypeMismatch(member.Value.Span, "comptime integer", boundValue.Type.Name);
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
        return new EnumTypeSymbol(name, backingType, members, isOpen);
    }

    private TypeSymbol BindBitfieldType(BitfieldTypeSyntax bitfieldTypeSyntax, string? aliasName)
    {
        TypeSymbol backingType = BindType(bitfieldTypeSyntax.BackingType);
        if (!backingType.IsInteger)
            _diagnostics.ReportTypeMismatch(bitfieldTypeSyntax.BackingType.Span, "integer", backingType.Name);

        Dictionary<string, TypeSymbol> fields = new(StringComparer.Ordinal);
        Dictionary<string, AggregateMemberSymbol> members = new(StringComparer.Ordinal);
        int bitOffset = 0;
        int backingWidth = TypeFacts.TryGetIntegerWidth(backingType, out int width) ? width : 0;

        foreach (StructFieldSyntax field in bitfieldTypeSyntax.Fields)
        {
            TypeSymbol fieldType = BindType(field.Type);
            if (!fields.TryAdd(field.Name.Text, fieldType))
            {
                _diagnostics.ReportSymbolAlreadyDeclared(field.Name.Span, field.Name.Text);
                continue;
            }

            if (!TypeFacts.TryGetBitfieldFieldWidth(fieldType, out int fieldWidth))
            {
                _diagnostics.ReportTypeMismatch(field.Type.Span, "bitfield scalar", fieldType.Name);
                fieldWidth = 0;
            }

            if (bitOffset + fieldWidth > backingWidth)
                _diagnostics.ReportBitfieldWidthOverflow(field.Span, aliasName ?? "<anon-bitfield>", field.Name.Text, bitOffset + fieldWidth, backingWidth);

            members[field.Name.Text] = new AggregateMemberSymbol(field.Name.Text, fieldType, byteOffset: 0, bitOffset, fieldWidth, isBitfield: true);
            bitOffset += fieldWidth;
        }

        string name = aliasName ?? $"<anon-bitfield#{++_anonymousStructIndex}>";
        return new BitfieldTypeSymbol(name, backingType, fields, members);
    }

    private TypeSymbol BindNamedType(NamedTypeSyntax namedType)
    {
        bool isBuiltinTypeName = BuiltinTypes.TryGet(namedType.Name.Text, out _);
        Debug.Assert(!isBuiltinTypeName, "Builtin type keywords should bind as PrimitiveTypeSyntax, not NamedTypeSyntax.");
        return ResolveTypeAlias(namedType.Name.Text, namedType.Name.Span);
    }

    private TypeSymbol BindQualifiedType(QualifiedTypeSyntax qualifiedType)
    {
        string qualifiedName = string.Join('.', qualifiedType.Parts.Select(static part => part.Text));
        Token root = qualifiedType.Parts[0];
        if (!_globalScope.TryLookup(root.Text, out Symbol? symbol) || symbol is not ModuleSymbol moduleSymbol)
        {
            _diagnostics.ReportUndefinedType(qualifiedType.Span, qualifiedName);
            return BuiltinTypes.Unknown;
        }

        ImportedModule module = moduleSymbol.Module;
        for (int i = 1; i < qualifiedType.Parts.Count - 1; i++)
        {
            Token segment = qualifiedType.Parts[i];
            if (!module.ImportedModules.TryGetValue(segment.Text, out ImportedModule? nestedModule))
            {
                _diagnostics.ReportUndefinedType(segment.Span, qualifiedName);
                return BuiltinTypes.Unknown;
            }

            module = nestedModule;
        }

        Token finalSegment = qualifiedType.Parts[^1];
        if (!module.ExportedTypes.TryGetValue(finalSegment.Text, out TypeSymbol? resolvedType))
        {
            _diagnostics.ReportUndefinedType(finalSegment.Span, qualifiedName);
            return BuiltinTypes.Unknown;
        }

        return resolvedType;
    }

    private TypeSymbol ResolveTypeAlias(string aliasName, TextSpan span)
    {
        if (_resolvedTypeAliases.TryGetValue(aliasName, out TypeSymbol? resolved))
            return resolved;

        if (!_typeAliases.TryGetValue(aliasName, out TypeAliasSymbol? alias))
        {
            _diagnostics.ReportUndefinedType(span, aliasName);
            return BuiltinTypes.Unknown;
        }

        if (!_typeAliasResolutionStack.Add(aliasName))
        {
            _diagnostics.ReportUndefinedType(span, aliasName);
            return BuiltinTypes.Unknown;
        }

        TypeSymbol boundType = BindType(alias.Syntax.Type, alias.Syntax.Name.Text);
        _typeAliasResolutionStack.Remove(aliasName);
        _resolvedTypeAliases[aliasName] = boundType;
        return boundType;
    }

    private static TypeSymbol BestNumericType(TypeSymbol left, TypeSymbol right)
    {
        if (left.IsUnknown || right.IsUnknown)
            return BuiltinTypes.Unknown;
        if (left == BuiltinTypes.IntegerLiteral && right == BuiltinTypes.IntegerLiteral)
            return BuiltinTypes.IntegerLiteral;
        if (left == BuiltinTypes.IntegerLiteral)
            return right;
        return left;
    }

    private static bool IsComparable(TypeSymbol left, TypeSymbol right)
    {
        if (left.IsUnknown || right.IsUnknown)
            return true;
        if (left.IsUndefinedLiteral || right.IsUndefinedLiteral)
            return true;
        if (left.IsInteger && right.IsInteger)
            return true;
        if (left.IsBool && right.IsBool)
            return true;
        return left.Name == right.Name;
    }

    private static bool IsAssignable(TypeSymbol target, TypeSymbol source)
    {
        if (target.IsUnknown || source.IsUnknown)
            return true;
        if (source.IsUndefinedLiteral)
            return true;

        if (target is EnumTypeSymbol targetEnum && source is EnumTypeSymbol sourceEnum)
            return ReferenceEquals(targetEnum, sourceEnum);

        if (target is BitfieldTypeSymbol targetBitfield && source is BitfieldTypeSymbol sourceBitfield)
            return ReferenceEquals(targetBitfield, sourceBitfield);

        if (target.IsInteger && source.IsInteger)
            return true;
        if (target.IsBool && source.IsBool)
            return true;

        // String literal → [N]u8 coercion (length check deferred to BindConversion)
        if (ReferenceEquals(source, BuiltinTypes.String)
            && target is ArrayTypeSymbol { ElementType: var elemType, Length: not null }
            && ReferenceEquals(elemType, BuiltinTypes.U8))
        {
            return true;
        }

        // String literal → [*]<storage> const u8 coercion
        if (ReferenceEquals(source, BuiltinTypes.String)
            && target is MultiPointerTypeSymbol { PointeeType: var pointeeType, IsConst: true }
            && ReferenceEquals(pointeeType, BuiltinTypes.U8))
        {
            return true;
        }

        if (target is PointerLikeTypeSymbol targetPointer && source is PointerLikeTypeSymbol sourcePointer)
            return IsPointerAssignable(targetPointer, sourcePointer);

        if (target is StructTypeSymbol or UnionTypeSymbol)
            return ReferenceEquals(target, source);

        return target.Name == source.Name;
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

    private void PushLoop(LoopContext kind) => _loopStack.Push(kind);

    private void PopLoop()
    {
        if (_loopStack.Count > 0)
            _loopStack.Pop();
    }

    private static FunctionKind GetFunctionKind(TokenKind? kind) => kind switch
    {
        TokenKind.LeafKeyword => FunctionKind.Leaf,
        TokenKind.InlineKeyword => FunctionKind.Inline,
        TokenKind.NoinlineKeyword => FunctionKind.Noinline,
        TokenKind.RecKeyword => FunctionKind.Rec,
        TokenKind.CoroKeyword => FunctionKind.Coro,
        TokenKind.ComptimeKeyword => FunctionKind.Comptime,
        TokenKind.Int1Keyword => FunctionKind.Int1,
        TokenKind.Int2Keyword => FunctionKind.Int2,
        TokenKind.Int3Keyword => FunctionKind.Int3,
        _ => FunctionKind.Default,
    };

    private enum LoopContext
    {
        Regular,
        Rep,
    }
}
