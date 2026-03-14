using Blade;
using Blade.IR;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;

namespace Blade.Tests;

[TestFixture]
public class WriterAndSymbolTests
{
    private static readonly TextSpan Span = new(0, 0);

    private static VariableSymbol CreateVariable(string name, VariableStorageClass storageClass, VariableScopeKind scopeKind, object? initializer = null)
    {
        return new VariableSymbol(name, BuiltinTypes.U32, isConst: false, storageClass, scopeKind, isExtern: false, fixedAddress: null, alignment: null);
    }

    [Test]
    public void VariableSymbol_ReportsAutomaticAndGlobalStorageProperties()
    {
        VariableSymbol local = CreateVariable("local", VariableStorageClass.Automatic, VariableScopeKind.Local);
        VariableSymbol topLevel = CreateVariable("top", VariableStorageClass.Automatic, VariableScopeKind.TopLevelAutomatic);
        VariableSymbol globalReg = CreateVariable("global_reg", VariableStorageClass.Reg, VariableScopeKind.GlobalStorage);
        VariableSymbol globalHub = CreateVariable("global_hub", VariableStorageClass.Hub, VariableScopeKind.GlobalStorage);

        Assert.That(local.IsAutomatic, Is.True);
        Assert.That(topLevel.IsAutomatic, Is.True);
        Assert.That(globalReg.IsAutomatic, Is.False);
        Assert.That(globalReg.IsGlobalStorage, Is.True);
        Assert.That(globalReg.UsesGlobalRegisterStorage, Is.True);
        Assert.That(globalHub.UsesGlobalRegisterStorage, Is.False);
    }

    [Test]
    public void DumpContentBuilder_ReturnsFinalAssemblyWhenNoExplicitDumpFlagsAreSet()
    {
        BoundProgram program = new([], [], [], new Dictionary<string, TypeSymbol>(), new Dictionary<string, FunctionSymbol>());
        MirModule mir = new([]);
        LirModule lir = new([]);
        AsmModule asm = new([]);
        IrBuildResult build = new(program, mir, mir, lir, lir, asm, asm, "DAT\n");

        Dictionary<string, string> dumps = DumpContentBuilder.Build(new DumpSelection(), build);

        Assert.That(dumps.Keys, Is.EqualTo(new[] { "40_final.spin2" }));
        Assert.That(dumps["40_final.spin2"], Is.EqualTo("DAT\n"));
    }

    [Test]
    public void MirLirAndAsmTextWriters_FormatRepresentativeNodes()
    {
        StoragePlace place = new(CreateVariable("mem-slot", VariableStorageClass.Reg, VariableScopeKind.GlobalStorage), StoragePlaceKind.AllocatableGlobalRegister, fixedAddress: null, staticInitializer: 5);

        MirModule mir = new([
            new MirFunction("mir_fn", isEntryPoint: true, FunctionKind.Leaf, [BuiltinTypes.U32],
            [
                new MirBlock("bb0", [new MirBlockParameter(new MirValueId(0), "p", BuiltinTypes.U32)],
                [
                    new MirLoadPlaceInstruction(new MirValueId(1), BuiltinTypes.U32, place, Span),
                    new MirPseudoInstruction("pin", [new MirValueId(1)], hasSideEffects: true, Span),
                ], new MirUnreachableTerminator(Span)),
            ]),
        ]);

        LirModule lir = new([
            new LirFunction("lir_fn", isEntryPoint: false, FunctionKind.Default, [],
            [
                new LirBlock("bb0", [],
                [
                    new LirOpInstruction("mov", new LirVirtualRegister(0), BuiltinTypes.U32,
                        [new LirImmediateOperand("hello", BuiltinTypes.String)],
                        hasSideEffects: false, predicate: "if_c", writesC: true, writesZ: true, Span),
                ], new LirUnreachableTerminator(Span)),
            ]),
        ]);

        AsmModule asm = new([
            new AsmFunction("asm_fn", isEntryPoint: false, CallingConventionTier.General,
            [
                new AsmDirectiveNode("org 0"),
                new AsmLabelNode("asm_fn_bb0"),
                new AsmCommentNode("test"),
                new AsmImplicitUseNode([new AsmRegisterOperand(1)]),
                new AsmInstructionNode("ADD", [new AsmRegisterOperand(1), new AsmImmediateOperand(5)], predicate: "IF_C", flagEffect: AsmFlagEffect.WCZ),
                new AsmInlineTextNode("MOV _r1, #1"),
            ]),
        ]);

        string mirText = MirTextWriter.Write(mir);
        string lirText = LirTextWriter.Write(lir);
        string asmText = AsmTextWriter.Write(asm);

        Assert.That(mirText, Does.Contain("load.place"));
        Assert.That(mirText, Does.Contain("pseudo pin"));
        Assert.That(mirText, Does.Contain("unreachable"));

        Assert.That(lirText, Does.Contain("[if_c] mov"));
        Assert.That(lirText, Does.Contain("flags=CZ"));
        Assert.That(lirText, Does.Contain("\"hello\":string"));
        Assert.That(lirText, Does.Contain("unreachable"));

        Assert.That(asmText, Does.Contain(".org 0"));
        Assert.That(asmText, Does.Contain("' test"));
        Assert.That(asmText, Does.Contain(".use %r1"));
        Assert.That(asmText, Does.Contain("IF_C ADD %r1, #5 WCZ"));
        Assert.That(asmText, Does.Contain("MOV _r1, #1"));
    }
}
