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
            new FunctionSymbol(name, kind),
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
        return new AsmFunction(sourceFunction, ccTier, nodes);
    }

    public static VariableSymbol CreateVariableSymbol(
        string name,
        BladeType? type = null,
        VariableStorageClass storageClass = VariableStorageClass.Automatic,
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
            VariableScopeKind.GlobalStorage => new GlobalVariableSymbol(name, effectiveType, isConst, storageClass, isExtern, fixedAddress, alignment, SourceSpan.Synthetic()),
            _ => throw new InvalidOperationException($"Unsupported variable scope kind '{scopeKind}'."),
        };
    }

    public static StoragePlace CreateStoragePlace(
        string name,
        StoragePlaceKind kind = StoragePlaceKind.AllocatableGlobalRegister,
        BladeType? type = null,
        VariableStorageClass storageClass = VariableStorageClass.Reg,
        VariableScopeKind scopeKind = VariableScopeKind.GlobalStorage,
        bool isConst = false,
        bool isExtern = false,
        int? fixedAddress = null,
        int? alignment = null)
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
        return new StoragePlace(symbol, kind, fixedAddress, emittedName: $"g_{name}");
    }

    public static BoundModule CreateBoundModule(
        string resolvedFilePath = "/tmp/test.blade",
        IReadOnlyList<BoundStatement>? topLevelStatements = null,
        IReadOnlyList<GlobalVariableSymbol>? globalVariables = null,
        IReadOnlyList<BoundFunctionMember>? functions = null,
        IReadOnlyDictionary<string, Symbol>? exportedSymbols = null)
    {
        return new BoundModule(
            resolvedFilePath,
            new CompilationUnitSyntax([], new Token(TokenKind.EndOfFile, new TextSpan(0, 0), string.Empty)),
            topLevelStatements ?? [],
            globalVariables ?? [],
            functions ?? [],
            exportedSymbols ?? CreateExports(globalVariables, functions));
    }

    public static BoundProgram CreateBoundProgram(
        string resolvedFilePath = "/tmp/test.blade",
        IReadOnlyList<BoundStatement>? topLevelStatements = null,
        IReadOnlyList<GlobalVariableSymbol>? globalVariables = null,
        IReadOnlyList<BoundFunctionMember>? functions = null,
        IReadOnlyDictionary<string, Symbol>? exportedSymbols = null,
        IReadOnlyList<BoundModule>? modules = null)
    {
        BoundModule rootModule = CreateBoundModule(
            resolvedFilePath,
            topLevelStatements,
            globalVariables,
            functions,
            exportedSymbols);
        IReadOnlyList<BoundModule> effectiveModules = modules ?? [rootModule];
        return new BoundProgram(
            rootModule,
            effectiveModules,
            globalVariables ?? [],
            functions ?? []);
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
