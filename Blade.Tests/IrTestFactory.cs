using System.Collections.Generic;
using Blade.IR;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics;

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
        IReadOnlyList<TypeSymbol> returnTypes,
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
        IReadOnlyList<TypeSymbol> returnTypes,
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
        IReadOnlyList<TypeSymbol>? returnTypes = null,
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
        TypeSymbol? type = null,
        VariableStorageClass storageClass = VariableStorageClass.Automatic,
        VariableScopeKind scopeKind = VariableScopeKind.Local,
        bool isConst = false,
        bool isExtern = false,
        int? fixedAddress = null,
        int? alignment = null)
    {
        return new VariableSymbol(
            name,
            type ?? BuiltinTypes.U32,
            isConst,
            storageClass,
            scopeKind,
            isExtern,
            fixedAddress,
            alignment);
    }

    public static StoragePlace CreateStoragePlace(
        string name,
        StoragePlaceKind kind = StoragePlaceKind.AllocatableGlobalRegister,
        TypeSymbol? type = null,
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
}
