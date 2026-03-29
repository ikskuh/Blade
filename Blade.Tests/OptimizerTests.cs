using System.Linq;
using Blade.IR;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Source;

namespace Blade.Tests;

[TestFixture]
public class OptimizerTests
{
    private static readonly TextSpan Span = new(0, 0);

    private static StoragePlace CreatePlace(string name)
    {
        VariableSymbol symbol = new(name, BuiltinTypes.U32, isConst: false, VariableStorageClass.Reg, VariableScopeKind.GlobalStorage, isExtern: false, fixedAddress: null, alignment: null);
        return new StoragePlace(symbol, StoragePlaceKind.AllocatableGlobalRegister, fixedAddress: null, staticInitializer: null, emittedName: $"g_{name}");
    }

    [Test]
    public void MirOptimizer_ThreadsMergesAndRemovesUnreachableBlocks()
    {
        MirValueId seed = MirValue(0);
        MirValueId threaded = MirValue(1);
        MirValueId merged = MirValue(2);
        StoragePlace resultPlace = CreatePlace("result");
        MirBlockRef bb0 = new();
        MirBlockRef bb1 = new();
        MirBlockRef bb2 = new();
        MirBlockRef dead = new();

        MirModule module = new([
            CreateMirFunction("f", isEntryPoint: false, FunctionKind.Default, [],
            [
                new MirBlock(bb0, [], [new MirConstantInstruction(seed, BuiltinTypes.U32, 7, Span)],
                    new MirGotoTerminator(bb1, [seed], Span)),
                new MirBlock(bb1, [new MirBlockParameter(threaded, "x", BuiltinTypes.U32)], [],
                    new MirGotoTerminator(bb2, [threaded], Span)),
                new MirBlock(bb2, [new MirBlockParameter(merged, "y", BuiltinTypes.U32)],
                    [new MirStorePlaceInstruction(resultPlace, merged, Span)],
                    new MirReturnTerminator([], Span)),
                new MirBlock(dead, [], [], new MirReturnTerminator([], Span)),
            ]),
        ]);

        MirModule optimized = MirOptimizer.Optimize(module, maxIterations: 4, enabledOptimizations:
        [
            OptimizationRegistry.GetMirOptimization("const-prop")!,
            OptimizationRegistry.GetMirOptimization("copy-prop")!,
            OptimizationRegistry.GetMirOptimization("cfg-simplify")!,
            OptimizationRegistry.GetMirOptimization("dce")!,
        ]);
        MirFunction function = optimized.Functions[0];

        Assert.That(function.Blocks, Has.Count.EqualTo(1));
        Assert.That(function.Blocks[0].Ref, Is.SameAs(bb0));
        Assert.That(function.Blocks[0].Instructions, Has.Count.EqualTo(2));
        Assert.That(function.Blocks[0].Instructions[1], Is.TypeOf<MirStorePlaceInstruction>());

        MirStorePlaceInstruction store = (MirStorePlaceInstruction)function.Blocks[0].Instructions[1];
        Assert.That(store.Value, Is.EqualTo(seed));
    }

    [Test]
    public void LirOptimizer_PropagatesCopiesAcrossGotoArgumentsAndMergesBlocks()
    {
        LirVirtualRegister seed = LirRegister(0);
        LirVirtualRegister copy = LirRegister(1);
        LirVirtualRegister threaded = LirRegister(2);
        LirVirtualRegister merged = LirRegister(3);
        StoragePlace resultPlace = CreatePlace("result");
        LirBlockRef bb0 = new();
        LirBlockRef bb1 = new();
        LirBlockRef bb2 = new();
        LirBlockRef dead = new();

        LirModule module = new([
            CreateLirFunction("f", isEntryPoint: false, FunctionKind.Default, [],
            [
                new LirBlock(bb0, [],
                [
                    new LirOpInstruction(new LirConstOperation(), seed, BuiltinTypes.U32,
                        [new LirImmediateOperand(7, BuiltinTypes.U32)],
                        hasSideEffects: false, predicate: null, writesC: false, writesZ: false, Span),
                    new LirOpInstruction(new LirMovOperation(), copy, BuiltinTypes.U32,
                        [new LirRegisterOperand(seed)],
                        hasSideEffects: false, predicate: null, writesC: false, writesZ: false, Span),
                ], new LirGotoTerminator(bb1, [new LirRegisterOperand(copy)], Span)),
                new LirBlock(bb1, [new LirBlockParameter(threaded, "x", BuiltinTypes.U32)], [],
                    new LirGotoTerminator(bb2, [new LirRegisterOperand(threaded)], Span)),
                new LirBlock(bb2, [new LirBlockParameter(merged, "y", BuiltinTypes.U32)],
                [
                    new LirOpInstruction(new LirStorePlaceOperation(), destination: null, resultType: null,
                        [new LirPlaceOperand(resultPlace), new LirRegisterOperand(merged)],
                        hasSideEffects: true, predicate: null, writesC: false, writesZ: false, Span),
                ], new LirReturnTerminator([], Span)),
                new LirBlock(dead, [], [], new LirReturnTerminator([], Span)),
            ]),
        ]);

        LirModule optimized = LirOptimizer.Optimize(module, maxIterations: 4, enabledOptimizations: OptimizationRegistry.AllLirOptimizations);
        LirFunction function = optimized.Functions[0];

        Assert.That(function.Blocks, Has.Count.EqualTo(1));
        Assert.That(function.Blocks[0].Instructions, Has.Count.EqualTo(2));
        Assert.That(function.Blocks[0].Instructions.Any(i => i is LirOpInstruction op && op.DisplayName == "mov"), Is.False);

        LirOpInstruction store = (LirOpInstruction)function.Blocks[0].Instructions[1];
        Assert.That(store.Operands[1], Is.TypeOf<LirRegisterOperand>());
        Assert.That(((LirRegisterOperand)store.Operands[1]).Register, Is.EqualTo(seed));
    }

    [Test]
    public void AsmOptimizer_RemovesJumpToImmediatelyFollowingLabel()
    {
        ControlFlowLabelSymbol bb0 = new("f_bb0");
        ControlFlowLabelSymbol bb1 = new("f_bb1");

        AsmModule module = new([
            CreateAsmFunction("f", isEntryPoint: false, CallingConventionTier.General,
            [
                new AsmLabelNode(bb0),
                new AsmInstructionNode(P2Mnemonic.JMP, [new AsmSymbolOperand(bb1, AsmSymbolAddressingMode.Immediate)]),
                new AsmCommentNode("between"),
                new AsmLabelNode(bb1),
                new AsmInstructionNode(P2Mnemonic.NOP, []),
            ]),
        ]);

        AsmModule optimized = AsmOptimizer.Optimize(module, OptimizationRegistry.AllAsmOptimizations);
        AsmFunction function = optimized.Functions[0];

        Assert.That(function.Nodes.OfType<AsmInstructionNode>().Any(i => i.Opcode == "JMP"), Is.False);
    }

    [Test]
    public void AsmOptimizer_PreservesNonElidableHaltSentinel()
    {
        AsmModule module = new([
            CreateAsmFunction("f", isEntryPoint: true, CallingConventionTier.EntryPoint,
            [
                new AsmCommentNode("halt: endless loop"),
                new AsmInstructionNode(
                    P2Mnemonic.REP,
                    [new AsmImmediateOperand(1), new AsmImmediateOperand(0)],
                    isNonElidable: true),
                new AsmInstructionNode(P2Mnemonic.NOP, [], isNonElidable: true),
            ]),
        ]);

        AsmFunction function = AsmOptimizer.Optimize(module, OptimizationRegistry.AllAsmOptimizations).Functions[0];
        AsmInstructionNode[] instructions = function.Nodes.OfType<AsmInstructionNode>().ToArray();

        Assert.That(instructions, Has.Length.EqualTo(2));
        Assert.That(instructions[0].Opcode, Is.EqualTo("REP"));
        Assert.That(instructions[0].IsNonElidable, Is.True);
        Assert.That(instructions[1].Opcode, Is.EqualTo("NOP"));
        Assert.That(instructions[1].IsNonElidable, Is.True);
    }

    [Test]
    public void AsmOptimizer_ElidesPlainNopWhenNotMarkedNonElidable()
    {
        AsmModule module = new([
            CreateAsmFunction("f", isEntryPoint: false, CallingConventionTier.General,
            [
                new AsmInstructionNode(P2Mnemonic.NOP, []),
            ]),
        ]);

        AsmFunction function = AsmOptimizer.Optimize(module, OptimizationRegistry.AllAsmOptimizations).Functions[0];
        Assert.That(function.Nodes.OfType<AsmInstructionNode>(), Is.Empty);
    }

    [Test]
    public void AsmOptimizer_ElidesStraightLineMovChainWhenDeadAtFunctionEnd()
    {
        StoragePlace inputPlace = new(CreateVariableSymbol("input", scopeKind: VariableScopeKind.GlobalStorage, storageClass: VariableStorageClass.Reg), StoragePlaceKind.AllocatableGlobalRegister, fixedAddress: null, staticInitializer: null, emittedName: "input");
        StoragePlace outputPlace = new(CreateVariableSymbol("output", scopeKind: VariableScopeKind.GlobalStorage, storageClass: VariableStorageClass.Reg), StoragePlaceKind.AllocatableGlobalRegister, fixedAddress: null, staticInitializer: null, emittedName: "output");
        AsmRegisterOperand r1 = AsmRegister(1);
        AsmRegisterOperand r2 = AsmRegister(2);
        AsmSymbolOperand input = new(inputPlace, AsmSymbolAddressingMode.Register);
        AsmSymbolOperand output = new(outputPlace, AsmSymbolAddressingMode.Register);

        AsmModule module = new([
            CreateAsmFunction("f", isEntryPoint: false, CallingConventionTier.General,
            [
                new AsmLabelNode("f_bb0"),
                new AsmInstructionNode(P2Mnemonic.MOV, [r1, input]),
                new AsmInstructionNode(P2Mnemonic.MOV, [r2, r1]),
                new AsmInstructionNode(P2Mnemonic.MOV, [output, r2]),
            ]),
        ]);

        AsmFunction function = AsmOptimizer.Optimize(module, OptimizationRegistry.AllAsmOptimizations).Functions[0];
        AsmInstructionNode[] instructions = function.Nodes.OfType<AsmInstructionNode>().ToArray();

        Assert.That(instructions, Has.Length.EqualTo(1));
        Assert.That(instructions[0].Opcode, Is.EqualTo("MOV"));
        Assert.That(instructions[0].Operands[0], Is.TypeOf<AsmSymbolOperand>());
        Assert.That(instructions[0].Operands[1], Is.TypeOf<AsmSymbolOperand>());
        Assert.That(((AsmSymbolOperand)instructions[0].Operands[1]).Name, Is.EqualTo("input"));
    }

    [Test]
    public void AsmOptimizer_DoesNotElideCopyAcrossInlineAsmBarrier()
    {
        StoragePlace inputPlace = new(CreateVariableSymbol("input", scopeKind: VariableScopeKind.GlobalStorage, storageClass: VariableStorageClass.Reg), StoragePlaceKind.AllocatableGlobalRegister, fixedAddress: null, staticInitializer: null, emittedName: "input");
        StoragePlace outputPlace = new(CreateVariableSymbol("output", scopeKind: VariableScopeKind.GlobalStorage, storageClass: VariableStorageClass.Reg), StoragePlaceKind.AllocatableGlobalRegister, fixedAddress: null, staticInitializer: null, emittedName: "output");
        AsmRegisterOperand r1 = AsmRegister(1);
        AsmSymbolOperand input = new(inputPlace, AsmSymbolAddressingMode.Register);
        AsmSymbolOperand output = new(outputPlace, AsmSymbolAddressingMode.Register);

        AsmModule module = new([
            CreateAsmFunction("f", isEntryPoint: false, CallingConventionTier.General,
            [
                new AsmLabelNode("f_bb0"),
                new AsmInstructionNode(P2Mnemonic.MOV, [r1, input]),
                new AsmVolatileRegionBeginNode(),
                new AsmVolatileRegionEndNode(),
                new AsmInstructionNode(P2Mnemonic.MOV, [output, r1]),
            ]),
        ]);

        AsmFunction function = AsmOptimizer.Optimize(module, OptimizationRegistry.AllAsmOptimizations).Functions[0];
        AsmInstructionNode[] instructions = function.Nodes.OfType<AsmInstructionNode>().ToArray();

        Assert.That(instructions, Has.Length.EqualTo(2));
        Assert.That(instructions[0].Operands[0], Is.TypeOf<AsmRegisterOperand>());
        Assert.That(instructions[1].Operands[1], Is.TypeOf<AsmRegisterOperand>());
        Assert.That(((AsmRegisterOperand)instructions[1].Operands[1]).Register, Is.SameAs(r1.Register));
    }

    [Test]
    public void AsmOptimizer_RemovesDeadPureRegisterOnlyInlineAsmInstructions()
    {
        AsmRegisterOperand r1 = AsmRegister(1);
        AsmRegisterOperand r2 = AsmRegister(2);

        AsmModule module = new([
            CreateAsmFunction("f", isEntryPoint: false, CallingConventionTier.General,
            [
                new AsmLabelNode("f_bb0"),
                new AsmInstructionNode(P2Mnemonic.MOV, [r1, r2]),
                new AsmInstructionNode(P2Mnemonic.ADD, [r1, new AsmImmediateOperand(1)]),
            ]),
        ]);

        AsmFunction function = AsmOptimizer.Optimize(module, OptimizationRegistry.AllAsmOptimizations).Functions[0];
        Assert.That(function.Nodes.OfType<AsmInstructionNode>(), Is.Empty);
    }

    [Test]
    public void AsmOptimizer_DoesNotRemoveNonRegisterDestinationInstruction()
    {
        AsmRegisterOperand r1 = AsmRegister(1);

        AsmModule module = new([
            CreateAsmFunction("f", isEntryPoint: false, CallingConventionTier.General,
            [
                new AsmLabelNode("f_bb0"),
                new AsmInstructionNode(P2Mnemonic.MOV, [new AsmSymbolOperand(P2SpecialRegister.OUTA), r1]),
            ]),
        ]);

        AsmFunction function = AsmOptimizer.Optimize(module, OptimizationRegistry.AllAsmOptimizations).Functions[0];
        Assert.That(function.Nodes.OfType<AsmInstructionNode>().ToArray(), Has.Length.EqualTo(1));
    }

    [Test]
    public void AsmOptimizer_PreservesInstructionUsedByImplicitReturnUse()
    {
        AsmRegisterOperand r1 = AsmRegister(1);

        AsmModule module = new([
            CreateAsmFunction("f", isEntryPoint: false, CallingConventionTier.General,
            [
                new AsmLabelNode("f_bb0"),
                new AsmInstructionNode(P2Mnemonic.ADD, [r1, new AsmImmediateOperand(1)]),
                new AsmImplicitUseNode([r1]),
                new AsmInstructionNode(P2Mnemonic.RET, []),
            ]),
        ]);

        AsmFunction function = AsmOptimizer.Optimize(module, OptimizationRegistry.AllAsmOptimizations).Functions[0];
        Assert.That(function.Nodes.OfType<AsmInstructionNode>().Any(i => i.Opcode == "ADD"), Is.True);
    }

    [Test]
    public void LirOptimizer_RewritesInlineAsmBindingsDuringCopyPropagation()
    {
        LirVirtualRegister r0 = LirRegister(0);
        LirVirtualRegister r1 = LirRegister(1);

        LirModule module = new([
            CreateLirFunction("f", isEntryPoint: false, FunctionKind.Default, [],
            [
                new LirBlock(LirBlockRef("bb0"), [new LirBlockParameter(r0, "x", BuiltinTypes.U32)],
                [
                    new LirOpInstruction(new LirMovOperation(), r1, BuiltinTypes.U32,
                        [new LirRegisterOperand(r0)],
                        hasSideEffects: false, predicate: null, writesC: false, writesZ: false, Span),
                    new LirInlineAsmInstruction(
                        AsmVolatility.NonVolatile,
                        "TEST {x}, #1",
                        flagOutput: null,
                        parsedLines: [],
                        bindings: [new LirInlineAsmBinding(new InlineAsmBindingSlot("x"), CreateVariableSymbol("x"), new LirRegisterOperand(r1), InlineAsmBindingAccess.Read)],
                        Span),
                ], new LirReturnTerminator([], Span)),
            ]),
        ]);

        LirFunction function = LirOptimizer.Optimize(module, maxIterations: 4, enabledOptimizations: OptimizationRegistry.AllLirOptimizations).Functions[0];
        LirInlineAsmInstruction inlineAsm = function.Blocks[0].Instructions.OfType<LirInlineAsmInstruction>().Single();

        Assert.That(inlineAsm.Bindings[0].Operand, Is.TypeOf<LirRegisterOperand>());
        Assert.That(((LirRegisterOperand)inlineAsm.Bindings[0].Operand).Register, Is.EqualTo(r0));
        Assert.That(function.Blocks[0].Instructions.OfType<LirOpInstruction>().Any(op => op.DisplayName == "mov"), Is.False);
    }

    [Test]
    public void LirOptimizer_KeepsDefinitionUsedByInlineAsmBinding()
    {
        LirVirtualRegister r0 = LirRegister(0);

        LirModule module = new([
            CreateLirFunction("f", isEntryPoint: false, FunctionKind.Default, [],
            [
                new LirBlock(LirBlockRef("bb0"), [],
                [
                    new LirOpInstruction(new LirConstOperation(), r0, BuiltinTypes.U32,
                        [new LirImmediateOperand(13, BuiltinTypes.U32)],
                        hasSideEffects: false, predicate: null, writesC: false, writesZ: false, Span),
                    new LirInlineAsmInstruction(
                        AsmVolatility.Volatile,
                        "MOV INA, {x}",
                        flagOutput: null,
                        parsedLines: [],
                        bindings: [new LirInlineAsmBinding(new InlineAsmBindingSlot("x"), CreateVariableSymbol("x"), new LirRegisterOperand(r0), InlineAsmBindingAccess.ReadWrite)],
                        Span),
                ], new LirReturnTerminator([], Span)),
            ]),
        ]);

        LirFunction function = LirOptimizer.Optimize(module, maxIterations: 4, enabledOptimizations: OptimizationRegistry.AllLirOptimizations).Functions[0];
        Assert.That(function.Blocks[0].Instructions.OfType<LirOpInstruction>().Any(op => op.DisplayName == "const"), Is.True);
    }

    [Test]
    public void LirOptimizer_DoesNotPropagateAliasAcrossInlineAsmWriteBinding()
    {
        LirVirtualRegister r0 = LirRegister(0);
        LirVirtualRegister r1 = LirRegister(1);
        LirVirtualRegister r2 = LirRegister(2);

        LirModule module = new([
            CreateLirFunction("f", isEntryPoint: false, FunctionKind.Default, [BuiltinTypes.U32],
            [
                new LirBlock(LirBlockRef("bb0"), [new LirBlockParameter(r0, "x", BuiltinTypes.U32)],
                [
                    new LirOpInstruction(new LirMovOperation(), r1, BuiltinTypes.U32,
                        [new LirRegisterOperand(r0)],
                        hasSideEffects: false, predicate: null, writesC: false, writesZ: false, Span),
                    new LirInlineAsmInstruction(
                        AsmVolatility.NonVolatile,
                        "MOV {x}, #1",
                        flagOutput: null,
                        parsedLines: [],
                        bindings: [new LirInlineAsmBinding(new InlineAsmBindingSlot("x"), CreateVariableSymbol("x"), new LirRegisterOperand(r1), InlineAsmBindingAccess.Write)],
                        Span),
                    new LirOpInstruction(new LirMovOperation(), r2, BuiltinTypes.U32,
                        [new LirRegisterOperand(r1)],
                        hasSideEffects: false, predicate: null, writesC: false, writesZ: false, Span),
                ], new LirReturnTerminator([new LirRegisterOperand(r2)], Span)),
            ]),
        ]);

        LirFunction function = LirOptimizer.Optimize(module, maxIterations: 4, enabledOptimizations: OptimizationRegistry.AllLirOptimizations).Functions[0];
        LirReturnTerminator ret = (LirReturnTerminator)function.Blocks[0].Terminator;

        Assert.That(ret.Values[0], Is.TypeOf<LirRegisterOperand>());
        Assert.That(((LirRegisterOperand)ret.Values[0]).Register, Is.EqualTo(r1));
    }
}
