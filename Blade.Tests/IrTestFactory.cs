using System;
using System.Collections.Generic;
using System.Linq;
using Blade.IR;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;
using Blade.Syntax;
using Blade.Syntax.Nodes;

namespace Blade.Tests;

internal static class IrTestFactory
{
    private static readonly Dictionary<int, MirValueId> MirValues = [];
    private static readonly Dictionary<int, LirVirtualRegister> LirRegisters = [];
    private static readonly Dictionary<int, VirtualAsmRegister> AsmRegisters = [];

    public static MirValueId MirValue(int id)
    {
        if (!MirValues.TryGetValue(id, out MirValueId? value))
        {
            value = new MirValueId();
            MirValues.Add(id, value);
        }

        return value;
    }

    public static LirVirtualRegister LirRegister(int id)
    {
        if (!LirRegisters.TryGetValue(id, out LirVirtualRegister? register))
        {
            register = new LirVirtualRegister();
            LirRegisters.Add(id, register);
        }

        return register;
    }

    public static AsmRegisterOperand AsmRegister(int id)
    {
        if (!AsmRegisters.TryGetValue(id, out VirtualAsmRegister? register))
        {
            register = new VirtualAsmRegister();
            AsmRegisters.Add(id, register);
        }

        return new AsmRegisterOperand(register);
    }

    public static MirBlockRef MirBlockRef(string name) => new();

    public static LirBlockRef LirBlockRef(string name) => new();

    public static FunctionDeclarationSyntax CreateFunctionDeclarationSyntax(string name)
    {
        TextSpan span = new(0, 0);
        Token fnKeyword = new(TokenKind.FnKeyword, span, "fn");
        Token identifier = new(TokenKind.Identifier, span, name);
        Token openParen = new(TokenKind.OpenParen, span, "(");
        Token closeParen = new(TokenKind.CloseParen, span, ")");
        Token openBrace = new(TokenKind.OpenBrace, span, "{");
        Token closeBrace = new(TokenKind.CloseBrace, span, "}");
        BlockStatementSyntax body = new(openBrace, [], closeBrace);
        return new FunctionDeclarationSyntax(
            null,
            [],
            fnKeyword,
            identifier,
            openParen,
            new SeparatedSyntaxList<ParameterSyntax>([]),
            closeParen,
            arrow: null,
            returnSpec: null,
            metadata: null,
            body);
    }

    public static MirFunction CreateMirFunction(
        string name,
        bool isEntryPoint,
        FunctionKind kind,
        IReadOnlyList<BladeType> returnTypes,
        IReadOnlyList<MirBlock> blocks,
        IReadOnlyList<ReturnSlot>? returnSlots = null,
        IReadOnlyDictionary<MirValueId, MirFlag>? flagValues = null)
    {
        return new MirFunction(
            new FunctionSymbol(name, CreateFunctionDeclarationSyntax(name), kind, isTopLevel: false, storageClass: null, FunctionInliningPolicy.Default, SourceSpan.Synthetic()),
            isEntryPoint,
            returnTypes,
            blocks,
            returnSlots,
            flagValues);
    }

    public static LirFunction CreateLirFunction(
        string name,
        bool isEntryPoint,
        FunctionKind kind,
        IReadOnlyList<BladeType> returnTypes,
        IReadOnlyList<LirBlock> blocks,
        IReadOnlyList<ReturnSlot>? returnSlots = null)
    {
        MirFunction sourceFunction = CreateMirFunction(
            name,
            isEntryPoint,
            kind,
            returnTypes,
            [],
            returnSlots);
        return new LirFunction(sourceFunction, blocks);
    }

    public static AsmFunction CreateAsmFunction(
        string name,
        bool isEntryPoint,
        CallingConventionTier ccTier,
        IReadOnlyList<AsmNode> nodes,
        FunctionKind kind = FunctionKind.Default,
        IReadOnlyList<BladeType>? returnTypes = null,
        IReadOnlyList<ReturnSlot>? returnSlots = null)
    {
        LirFunction sourceFunction = CreateLirFunction(
            name,
            isEntryPoint,
            kind,
            returnTypes ?? [],
            [],
            returnSlots);
        TaskSymbol task = new(
            $"{name}_task",
            sourceFunction.Symbol,
            AddressSpace.Cog,
            SourceSpan.Synthetic());
        ImageDescriptor image = new(
            task,
            sourceFunction.Symbol,
            AddressSpace.Cog,
            isEntryPoint,
            [sourceFunction.Symbol],
            []);
        return new AsmFunction(image, sourceFunction, ccTier, nodes);
    }

    public static VariableSymbol CreateVariableSymbol(
        string name,
        BladeType? type = null,
        AddressSpace? storageClass = null,
        VariableScopeKind scopeKind = VariableScopeKind.Local,
        bool isConst = false,
        bool isExtern = false,
        int? fixedAddress = null,
        int? alignment = null)
    {
        BladeType effectiveType = type ?? BuiltinTypes.U32;
        return scopeKind switch
        {
            VariableScopeKind.Parameter => new ParameterVariableSymbol(name, effectiveType, SourceSpan.Synthetic()),
            VariableScopeKind.InlineAsmTemporary => new LocalVariableSymbol(name, effectiveType, isConst, isInlineAsmTemporary: true, SourceSpan.Synthetic()),
            VariableScopeKind.Local => new LocalVariableSymbol(name, effectiveType, isConst, sourceSpan: SourceSpan.Synthetic()),
            VariableScopeKind.GlobalStorage => new GlobalVariableSymbol(
                name,
                effectiveType,
                isConst,
                storageClass ?? throw new InvalidOperationException("Global storage variables require an explicit storage class."),
                declaringLayout: null,
                isExtern,
                fixedAddress.HasValue
                    ? new VirtualAddress(storageClass ?? throw new InvalidOperationException("Global storage variables require an explicit storage class."), fixedAddress.Value)
                    : null,
                alignment,
                SourceSpan.Synthetic()),
            _ => throw new InvalidOperationException($"Unsupported variable scope kind '{scopeKind}'."),
        };
    }

    public static StoragePlace CreateStoragePlace(
        string name,
        StoragePlacePlacement placement = StoragePlacePlacement.Allocatable,
        BladeType? type = null,
        AddressSpace storageClass = AddressSpace.Cog,
        VariableScopeKind scopeKind = VariableScopeKind.GlobalStorage,
        bool isConst = false,
        bool isExtern = false,
        int? fixedAddress = null,
        int? alignment = null,
        StoragePlaceRegisterRole? registerRole = null,
        P2SpecialRegister? specialRegisterAlias = null,
        string? emittedName = null)
    {
        VariableSymbol symbol = CreateVariableSymbol(
            name,
            type,
            storageClass,
            scopeKind,
            isConst,
            isExtern,
            fixedAddress,
            alignment);
        GlobalVariableSymbol globalSymbol = (GlobalVariableSymbol)symbol;
        StoragePlaceRegisterRole? effectiveRegisterRole = registerRole;
        if (!effectiveRegisterRole.HasValue
            && placement == StoragePlacePlacement.Allocatable
            && storageClass == AddressSpace.Cog)
        {
            effectiveRegisterRole = StoragePlaceRegisterRole.Global;
        }

        return new StoragePlace(globalSymbol, placement, effectiveRegisterRole, emittedName ?? $"g_{name}", specialRegisterAlias: specialRegisterAlias);
    }

    public static BoundModule CreateBoundModule(
        string resolvedFilePath = "/tmp/test.blade",
        IReadOnlyList<GlobalVariableSymbol>? globalVariables = null,
        IReadOnlyList<BoundFunctionMember>? functions = null,
        IReadOnlyDictionary<string, Symbol>? exportedSymbols = null)
    {
        IReadOnlyList<BoundFunctionMember> functionMembers = functions ?? [];
        return new BoundModule(
            resolvedFilePath,
            new CompilationUnitSyntax([], new Token(TokenKind.EndOfFile, new TextSpan(0, 0), string.Empty)),
            globalVariables ?? [],
            functionMembers,
            exportedSymbols ?? CreateExports(globalVariables, functionMembers));
    }

    public static BoundProgram CreateBoundProgram(
        string resolvedFilePath = "/tmp/test.blade",
        IReadOnlyList<BoundStatement>? entryPointStatements = null,
        IReadOnlyList<GlobalVariableSymbol>? globalVariables = null,
        IReadOnlyList<BoundFunctionMember>? functions = null,
        IReadOnlyDictionary<string, Symbol>? exportedSymbols = null,
        IReadOnlyList<BoundModule>? modules = null)
    {
        BoundFunctionMember entryPointFunction = CreateEntryPoint(entryPointStatements);
        TaskSymbol entryPoint = CreateEntryTask(entryPointFunction.Symbol);
        Dictionary<string, Symbol> rootExports = new(StringComparer.Ordinal);
        foreach ((string name, Symbol symbol) in exportedSymbols ?? CreateExports(globalVariables, functions))
            rootExports.Add(name, symbol);
        rootExports[entryPoint.Name] = entryPoint;

        BoundModule rootModule = CreateBoundModule(
            resolvedFilePath,
            globalVariables,
            [entryPointFunction, .. functions ?? []],
            rootExports);
        IReadOnlyList<BoundModule> effectiveModules = modules ?? [rootModule];
        List<GlobalVariableSymbol> effectiveGlobals = [];
        List<BoundFunctionMember> effectiveFunctions = [];
        foreach (BoundModule module in effectiveModules)
        {
            effectiveGlobals.AddRange(module.GlobalVariables);
            effectiveFunctions.AddRange(module.Functions);
        }

        if (!effectiveFunctions.Any(function => ReferenceEquals(function.Symbol, entryPoint.EntryFunction)))
            effectiveFunctions.Insert(0, entryPointFunction);

        return new BoundProgram(
            rootModule,
            entryPoint,
            entryPointFunction,
            entryPoint,
            entryPointFunction,
            effectiveModules,
            effectiveGlobals,
            effectiveFunctions);
    }

    public static BoundFunctionMember CreateEntryPoint(IReadOnlyList<BoundStatement>? statements = null)
    {
        BoundBlockStatement body = new(statements ?? [], new TextSpan(0, 0));
        FunctionSymbol entryFunction = new(
            "main",
            CreateFunctionDeclarationSyntax("main"),
            FunctionKind.Default,
            isTopLevel: false,
            AddressSpace.Cog,
            FunctionInliningPolicy.Default,
            SourceSpan.Synthetic());
        return new BoundFunctionMember(entryFunction, body, body.Span);
    }

    public static TaskSymbol CreateEntryTask(FunctionSymbol entryFunction)
    {
        return new TaskSymbol("main", Requires.NotNull(entryFunction), AddressSpace.Cog, SourceSpan.Synthetic());
    }

    public static ImagePlan CreateSingleEntryImagePlan(FunctionSymbol entryFunction)
    {
        Requires.NotNull(entryFunction);

        TaskSymbol task = CreateEntryTask(entryFunction);
        ImageDescriptor image = new(
            task,
            entryFunction,
            AddressSpace.Cog,
            isEntryImage: true,
            [entryFunction],
            []);
        return new ImagePlan([image], image);
    }

    public static ImagePlan CreateSingleEntryImagePlan(TaskSymbol task)
    {
        Requires.NotNull(task);

        ImageDescriptor image = new(
            task,
            task.EntryFunction,
            task.StorageClass,
            isEntryImage: true,
            [task.EntryFunction],
            []);
        return new ImagePlan([image], image);
    }

    public static CogResourceLayoutSet CreateEmptyCogResourceLayouts(ImagePlan imagePlan)
    {
        Requires.NotNull(imagePlan);
        ImagePlacement imagePlacement = ImagePlacer.Place(imagePlan);

        List<CogAddress> availableRegisters = [];
        for (int address = 0x1EF; address >= 0; address--)
            availableRegisters.Add(new CogAddress(address));

        CogResourceLayout entryLayout = new(imagePlacement.EntryImage, 0, availableRegisters, []);

        return new CogResourceLayoutSet(
            [entryLayout],
            entryLayout,
            new Dictionary<IAsmSymbol, MemoryAddress>(),
            new Dictionary<ImageDescriptor, CogResourceLayout>
            {
                [imagePlan.EntryImage] = entryLayout,
            },
            new Dictionary<StoragePlace, CogResourceLayout>(),
            0);
    }

    public static CogResourceLayoutSet CreateSimpleCogResourceLayouts(AsmModule module)
    {
        Requires.NotNull(module);
        Requires.That(module.Functions.Count > 0);

        ImagePlan imagePlan = CreateImagePlanFromModule(module);
        return CreateSimpleCogResourceLayouts(module, imagePlan, includeDefaultBladeHalt: false);
    }

    public static CogResourceLayoutSet CreateSimpleCogResourceLayouts(AsmModule module, bool includeDefaultBladeHalt)
    {
        Requires.NotNull(module);
        Requires.That(module.Functions.Count > 0);

        ImagePlan imagePlan = CreateImagePlanFromModule(module);
        return CreateSimpleCogResourceLayouts(module, imagePlan, includeDefaultBladeHalt);
    }

    public static CogResourceLayoutSet CreateSimpleCogResourceLayouts(AsmModule module, ImagePlan imagePlan, bool includeDefaultBladeHalt)
    {
        Requires.NotNull(module);
        Requires.NotNull(imagePlan);

        return CogResourcePlanner.Build(module, imagePlan, ImagePlacer.Place(imagePlan), new LayoutSolution([]), includeDefaultBladeHalt, diagnostics: null);
    }

    public static ImagePlan CreateImagePlanFromModule(AsmModule module)
    {
        Requires.NotNull(module);

        IReadOnlyList<ImageDescriptor> images = [.. module.Functions
            .Select(static function => function.OwningImage)
            .Distinct()];
        ImageDescriptor entryImage = module.Functions
            .FirstOrDefault(static function => function.IsEntryPoint)
            ?.OwningImage
            ?? module.Functions[0].OwningImage;
        return new ImagePlan(images, entryImage);
    }

    public static IReadOnlyDictionary<string, Symbol> CreateExports(
        IReadOnlyList<GlobalVariableSymbol>? globalVariables = null,
        IReadOnlyList<BoundFunctionMember>? functions = null,
        IReadOnlyDictionary<string, BoundModule>? importedModules = null,
        IReadOnlyDictionary<string, TypeSymbol>? typeSymbols = null)
    {
        Dictionary<string, Symbol> exports = new(StringComparer.Ordinal);
        if (typeSymbols is not null)
        {
            foreach (TypeSymbol typeSymbol in typeSymbols.Values)
                exports[typeSymbol.Name] = typeSymbol;
        }
        foreach (GlobalVariableSymbol globalVariable in globalVariables ?? [])
            exports[globalVariable.Name] = globalVariable;
        foreach (BoundFunctionMember function in functions ?? [])
            exports[function.Symbol.Name] = function.Symbol;
        if (importedModules is not null)
        {
            foreach ((string alias, BoundModule module) in importedModules)
                exports[alias] = new ModuleSymbol(alias, module, SourceSpan.Synthetic());
        }

        return exports;
    }
}
