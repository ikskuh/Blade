using Blade;
using Blade.IR;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;
using Blade.Syntax;
using Blade.Syntax.Nodes;
using System.Reflection;

namespace Blade.Tests;

[TestFixture]
public class WriterAndSymbolTests
{
    private static readonly TextSpan Span = new(0, 0);

    private static VariableSymbol CreateVariable(string name, VariableStorageClass? storageClass, VariableScopeKind scopeKind)
    {
        return IrTestFactory.CreateVariableSymbol(name, BuiltinTypes.U32, storageClass, scopeKind);
    }

    [Test]
    public void VariableSymbol_UsesConcreteVariableTypesForStorageSpecificProperties()
    {
        VariableSymbol local = CreateVariable("local", storageClass: null, VariableScopeKind.Local);
        VariableSymbol topLevel = CreateVariable("top", storageClass: null, VariableScopeKind.Local);
        VariableSymbol globalReg = CreateVariable("global_reg", VariableStorageClass.Cog, VariableScopeKind.GlobalStorage);
        VariableSymbol globalHub = CreateVariable("global_hub", VariableStorageClass.Hub, VariableScopeKind.GlobalStorage);
        ControlFlowLabelSymbol label = new("bb0");

        Assert.That(local, Is.TypeOf<LocalVariableSymbol>());
        Assert.That(topLevel, Is.TypeOf<LocalVariableSymbol>());
        Assert.That(globalReg, Is.TypeOf<GlobalVariableSymbol>());
        Assert.That(((GlobalVariableSymbol)globalReg).StorageClass, Is.EqualTo(VariableStorageClass.Cog));
        Assert.That(((GlobalVariableSymbol)globalHub).StorageClass, Is.EqualTo(VariableStorageClass.Hub));
        Assert.That(((GlobalVariableSymbol)globalReg).ScopeKind, Is.EqualTo(VariableScopeKind.GlobalStorage));
        Assert.That(((GlobalVariableSymbol)globalHub).Alignment, Is.Null);
        Assert.That(label.SymbolType, Is.EqualTo(SymbolType.ControlFlowLabel));
    }

    [Test]
    public void LirIndexOperations_AcceptArrayAndManyPointerShapes()
    {
        ArrayTypeSymbol arrayType = new(BuiltinTypes.U32, 2);
        MultiPointerTypeSymbol manyPointerType = new(BuiltinTypes.U32, isConst: false, VariableStorageClass.Cog);

        Assert.That(new LirLoadIndexOperation(arrayType, VariableStorageClass.Cog).IsValidResultType(BuiltinTypes.U32), Is.True);
        Assert.That(new LirLoadIndexOperation(manyPointerType, VariableStorageClass.Cog).IsValidResultType(BuiltinTypes.U32), Is.True);
        Assert.That(new LirStoreIndexOperation(arrayType, VariableStorageClass.Cog).IsValidResultType(BuiltinTypes.U32), Is.True);
        Assert.That(new LirStoreIndexOperation(manyPointerType, VariableStorageClass.Cog).IsValidResultType(BuiltinTypes.U32), Is.True);
    }

    [Test]
    public void AsmCurrentAddressSymbol_ReportsControlFlowLabelKind()
    {
        Type symbolType = typeof(FinalAssemblyWriter).Assembly.GetType("Blade.IR.Asm.AsmCurrentAddressSymbol", throwOnError: true)!;
        PropertyInfo instanceProperty = symbolType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!;
        object instance = instanceProperty.GetValue(null)!;
        PropertyInfo kindProperty = symbolType.GetProperty("SymbolType", BindingFlags.Public | BindingFlags.Instance)!;

        Assert.That(kindProperty.GetValue(instance), Is.EqualTo(SymbolType.ControlFlowLabel));
    }

    [Test]
    public void DumpContentBuilder_ReturnsFinalAssemblyWhenNoExplicitDumpFlagsAreSet()
    {
        BoundProgram program = IrTestFactory.CreateBoundProgram("/tmp/test.blade");
        MirModule mir = new([], [], []);
        LirModule lir = new([]);
        AsmModule asm = new([], [], []);
        IrBuildResult build = new(program, mir, mir, lir, lir, asm, asm, "DAT\n");

        Dictionary<string, string> dumps = DumpContentBuilder.Build(new DumpSelection(), build);

        Assert.That(dumps.Keys, Is.EqualTo(new[] { "40_final.spin2" }));
        Assert.That(dumps["40_final.spin2"], Is.EqualTo("DAT\n"));
    }

    [Test]
    public void BoundProgram_ExposesRootModuleEntryPointAndPath()
    {
        BoundProgram program = IrTestFactory.CreateBoundProgram("/tmp/test.blade");

        Assert.That(program.ResolvedFilePath, Is.EqualTo("/tmp/test.blade"));
        Assert.That(program.EntryPoint, Is.SameAs(program.RootModule.Constructor));
    }

    [Test]
    public void MirLirAndAsmTextWriters_FormatRepresentativeNodes()
    {
        StoragePlace place = IrTestFactory.CreateStoragePlace("mem-slot", emittedName: "g_mem_slot");

        MirModule mir = new([], [], [
            CreateMirFunction("mir_fn", isEntryPoint: true, FunctionKind.Leaf, [BuiltinTypes.U32],
            [
                new MirBlock(MirBlockRef("bb0"), [new MirBlockParameter(MirValue(0), "p", BuiltinTypes.U32)],
                [
                    new MirLoadPlaceInstruction(MirValue(1), BuiltinTypes.U32, place, Span),
                    new MirRepSetupInstruction(MirValue(1), Span),
                ], new MirUnreachableTerminator(Span)),
            ]),
        ]);

        LirModule lir = new([
            CreateLirFunction("lir_fn", isEntryPoint: false, FunctionKind.Default, [],
            [
                new LirBlock(LirBlockRef("bb0"), [],
                [
                    new LirOpInstruction(new LirMovOperation(), LirRegister(0), BuiltinTypes.U32,
                        [new LirImmediateOperand(BladeValue.U8Array([104, 101, 108, 108, 111]))],
                        hasSideEffects: false, predicate: P2ConditionCode.IF_C, writesC: true, writesZ: true, Span),
                ], new LirUnreachableTerminator(Span)),
            ]),
        ]);

        AsmModule asm = new(
            [],
            [],
            [
                CreateAsmFunction("asm_fn", isEntryPoint: false, CallingConventionTier.General,
                [
                    new AsmLabelNode("asm_fn_bb0"),
                    new AsmCommentNode("test"),
                    new AsmInstructionNode(P2Mnemonic.ADD, [AsmRegister(1), new AsmImmediateOperand(5)], P2ConditionCode.IF_C, flagEffect: P2FlagEffect.WCZ),
                    new AsmInstructionNode(P2Mnemonic.MOV, [AsmRegister(1), new AsmImmediateOperand(1)]),
                ]),
            ]);

        string mirText = MirTextWriter.Write(mir);
        string lirText = LirTextWriter.Write(lir);
        string asmText = AsmTextWriter.Write(asm);

        Assert.That(mirText, Does.Contain("load.place"));
        Assert.That(mirText, Does.Contain("rep.setup"));
        Assert.That(mirText, Does.Contain("unreachable"));

        Assert.That(lirText, Does.Contain("[if_c] mov"));
        Assert.That(lirText, Does.Contain("flags=CZ"));
        Assert.That(lirText, Does.Contain("[104, 101, 108, 108, 111]:[5]u8"));
        Assert.That(lirText, Does.Contain("unreachable"));

        Assert.That(asmText, Does.Contain("' test"));
        Assert.That(asmText, Does.Contain("IF_C ADD %r0, #5 WCZ"));
        Assert.That(asmText, Does.Contain("MOV %r0, #1"));
    }

    [Test]
    public void StoragePlace_DerivesPlacementLabelAndSymbolTypeFromOrthogonalMetadata()
    {
        StoragePlace allocatableRegister = IrTestFactory.CreateStoragePlace(
            "global_reg",
            placement: StoragePlacePlacement.Allocatable,
            storageClass: VariableStorageClass.Cog,
            emittedName: "g_global_reg");
        StoragePlace fixedLutAlias = IrTestFactory.CreateStoragePlace(
            "fixed_lut",
            placement: StoragePlacePlacement.FixedAlias,
            storageClass: VariableStorageClass.Lut,
            fixedAddress: 12,
            emittedName: "fixed_lut");
        StoragePlace externalHubAlias = IrTestFactory.CreateStoragePlace(
            "external_hub",
            placement: StoragePlacePlacement.ExternalAlias,
            storageClass: VariableStorageClass.Hub,
            isExtern: true,
            emittedName: "external_hub");

        Assert.That(allocatableRegister.IsAllocatable, Is.True);
        Assert.That(allocatableRegister.IsFixedAlias, Is.False);
        Assert.That(allocatableRegister.IsExternalAlias, Is.False);
        Assert.That(allocatableRegister.EmitsStorageLabel, Is.True);
        Assert.That(allocatableRegister.SymbolType, Is.EqualTo(SymbolType.RegVariable));

        Assert.That(fixedLutAlias.IsFixedAlias, Is.True);
        Assert.That(fixedLutAlias.IsExternalAlias, Is.False);
        Assert.That(fixedLutAlias.EmitsStorageLabel, Is.True);
        Assert.That(fixedLutAlias.SymbolType, Is.EqualTo(SymbolType.LutVariable));

        Assert.That(externalHubAlias.IsFixedAlias, Is.False);
        Assert.That(externalHubAlias.IsExternalAlias, Is.True);
        Assert.That(externalHubAlias.EmitsStorageLabel, Is.False);
        Assert.That(externalHubAlias.SymbolType, Is.EqualTo(SymbolType.HubVariable));
    }
}
