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

namespace Blade.Tests;

[TestFixture]
public class WriterAndSymbolTests
{
    private static readonly TextSpan Span = new(0, 0);

    private static VariableSymbol CreateVariable(string name, VariableStorageClass storageClass, VariableScopeKind scopeKind)
    {
        return IrTestFactory.CreateVariableSymbol(name, BuiltinTypes.U32, storageClass, scopeKind);
    }

    [Test]
    public void VariableSymbol_UsesConcreteVariableTypesForStorageSpecificProperties()
    {
        VariableSymbol local = CreateVariable("local", VariableStorageClass.Automatic, VariableScopeKind.Local);
        VariableSymbol topLevel = CreateVariable("top", VariableStorageClass.Automatic, VariableScopeKind.Local);
        VariableSymbol globalReg = CreateVariable("global_reg", VariableStorageClass.Reg, VariableScopeKind.GlobalStorage);
        VariableSymbol globalHub = CreateVariable("global_hub", VariableStorageClass.Hub, VariableScopeKind.GlobalStorage);

        Assert.That(local, Is.TypeOf<LocalVariableSymbol>());
        Assert.That(topLevel, Is.TypeOf<LocalVariableSymbol>());
        Assert.That(globalReg, Is.TypeOf<GlobalVariableSymbol>());
        Assert.That(((GlobalVariableSymbol)globalReg).UsesGlobalRegisterStorage, Is.True);
        Assert.That(((GlobalVariableSymbol)globalHub).UsesGlobalRegisterStorage, Is.False);
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
        StoragePlace place = new(CreateVariable("mem-slot", VariableStorageClass.Reg, VariableScopeKind.GlobalStorage), StoragePlaceKind.AllocatableGlobalRegister, fixedAddress: null, emittedName: "g_mem_slot");

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
}
