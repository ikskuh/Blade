using System.Collections.Generic;
using System.Text.RegularExpressions;
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
public class RegisterAllocatorTests
{
    [Test]
    public void CodegenPipeline_RemovesSelfMovesIntroducedByRegisterAllocation()
    {
        AsmRegisterOperand source = AsmRegister(1);
        AsmRegisterOperand copy = AsmRegister(2);

        AsmModule asmModule = CreateAsmModule(functions:
        [
            CreateAsmFunction("f", isEntryPoint: false, CallingConventionTier.General,
            [
                new AsmInstructionNode(P2Mnemonic.MOV, [copy, source]),
                new AsmInstructionNode(P2Mnemonic.MOV, [new AsmSymbolOperand(P2SpecialRegister.OUTA), copy]),
            ]),
        ]);

        IrBuildResult emit = CreateBuildResult(asmModule);
        CodegenPipeline.Emit(emit, new EmitOptions
        {
            EnabledAsmirOptimizations = [OptimizationRegistry.GetAsmOptimization("cleanup-self-mov")!],
        });

        Assert.That(emit.AssemblyText, Does.Not.Match(@"MOV\s+([A-Za-z_]\w*),\s+\1\b"), emit.AssemblyText);
        Assert.That(emit.AssemblyText, Does.Match(@"MOV\s+OUTA,\s+[A-Za-z_]\w*\b"), emit.AssemblyText);
    }

    [Test]
    public void CodegenPipeline_LeavesAllocatorSelfMovesWhenCleanupSelfMovDisabled()
    {
        AsmRegisterOperand source = AsmRegister(1);
        AsmRegisterOperand copy = AsmRegister(2);

        AsmModule asmModule = CreateAsmModule(functions:
        [
            CreateAsmFunction("f", isEntryPoint: false, CallingConventionTier.General,
            [
                new AsmInstructionNode(P2Mnemonic.MOV, [copy, source]),
                new AsmInstructionNode(P2Mnemonic.MOV, [new AsmSymbolOperand(P2SpecialRegister.OUTA), copy]),
            ]),
        ]);

        IrBuildResult emit = CreateBuildResult(asmModule);
        CodegenPipeline.Emit(emit, new EmitOptions
        {
            EnabledAsmirOptimizations = [],
        });

        Assert.That(emit.AssemblyText, Does.Match(@"MOV\s+([A-Za-z_]\w*),\s+\1\b"), emit.AssemblyText);
    }

    [Test]
    public void LivenessAnalyzer_TreatsPhiMoveSourcesAsInterfering()
    {
        AsmRegisterOperand srcA = AsmRegister(1);
        AsmRegisterOperand srcB = AsmRegister(2);
        AsmRegisterOperand dstA = AsmRegister(3);
        AsmRegisterOperand dstB = AsmRegister(4);
        ControlFlowLabelSymbol done = new("f_done");

        AsmFunction function = CreateAsmFunction("f", isEntryPoint: false, CallingConventionTier.General,
        [
            new AsmInstructionNode(P2Mnemonic.MOV, [dstA, srcA], condition: P2ConditionCode.IF_Z, isPhiMove: true),
            new AsmInstructionNode(P2Mnemonic.MOV, [dstB, srcB], condition: P2ConditionCode.IF_Z, isPhiMove: true),
            new AsmInstructionNode(P2Mnemonic.JMP, [new AsmSymbolOperand(done, AsmSymbolAddressingMode.Immediate)], condition: P2ConditionCode.IF_Z),
            new AsmLabelNode(done),
        ]);

        FunctionLiveness liveness = LivenessAnalyzer.Analyze(function);

        Assert.That(liveness.InterferenceGraph.ContainsKey(srcA.Value), Is.True);
        Assert.That(liveness.InterferenceGraph[srcA.Value].Contains(srcB.Value), Is.True);
    }

    [Test]
    public void LivenessAnalyzer_TreatsCompareSourcesAsInterfering()
    {
        AsmRegisterOperand srcA = AsmRegister(1);
        AsmRegisterOperand srcB = AsmRegister(2);

        AsmFunction function = CreateAsmFunction("f", isEntryPoint: false, CallingConventionTier.General,
        [
            new AsmInstructionNode(
                P2Mnemonic.CMP,
                [srcA, srcB],
                flagOutput: new AsmFlagOutput(P2FlagEffect.WC, null, null)),
            new AsmInstructionNode(P2Mnemonic.RET, []),
        ]);

        FunctionLiveness liveness = LivenessAnalyzer.Analyze(function);

        Assert.That(liveness.InterferenceGraph.ContainsKey(srcA.Value), Is.True);
        Assert.That(liveness.InterferenceGraph[srcA.Value].Contains(srcB.Value), Is.True);
    }

    [Test]
    public void LivenessAnalyzer_TreatsExplicitFlagInputsAsInterferingWithOperandReads()
    {
        AsmRegisterOperand dst = AsmRegister(1);
        AsmRegisterOperand src = AsmRegister(2);
        VirtualAsmFlag flagC = new();
        VirtualAsmFlag flagZ = new();

        AsmFunction function = CreateAsmFunction("f", isEntryPoint: false, CallingConventionTier.General,
        [
            new AsmInstructionNode(
                P2Mnemonic.ADD,
                [dst, src],
                flagInput: new AsmFlagInput(flagC, flagZ)),
            new AsmInstructionNode(P2Mnemonic.RET, []),
        ]);

        FunctionLiveness liveness = LivenessAnalyzer.Analyze(function);

        Assert.That(liveness.InterferenceGraph.ContainsKey(flagC), Is.True);
        Assert.That(liveness.InterferenceGraph[flagC].Contains(flagZ), Is.True);
        Assert.That(liveness.InterferenceGraph[flagC].Contains(src.Value), Is.True);
        Assert.That(liveness.InterferenceGraph[flagZ].Contains(dst.Value), Is.True);
    }

    [Test]
    public void LivenessAnalyzer_TreatsLoopHeaderValuesAsInterferingWithBodyTemps()
    {
        AsmRegisterOperand limit = AsmRegister(1);
        AsmRegisterOperand index = AsmRegister(2);
        AsmRegisterOperand carried = AsmRegister(3);
        AsmRegisterOperand temp = AsmRegister(4);
        AsmRegisterOperand updated = AsmRegister(5);
        ControlFlowLabelSymbol loopHeader = new("f_loop_header");
        ControlFlowLabelSymbol loopBody = new("f_loop_body");
        ControlFlowLabelSymbol done = new("f_done");

        AsmFunction function = CreateAsmFunction("f", isEntryPoint: false, CallingConventionTier.General,
        [
            new AsmInstructionNode(P2Mnemonic.MOV, [limit, new AsmImmediateOperand(8)]),
            new AsmInstructionNode(P2Mnemonic.MOV, [index, new AsmImmediateOperand(0)]),
            new AsmInstructionNode(P2Mnemonic.MOV, [carried, new AsmImmediateOperand(1)]),
            new AsmLabelNode(loopHeader),
            new AsmInstructionNode(
                P2Mnemonic.CMP,
                [index, limit],
                flagOutput: new AsmFlagOutput(P2FlagEffect.WC, null, null)),
            new AsmInstructionNode(
                P2Mnemonic.JMP,
                [new AsmSymbolOperand(done, AsmSymbolAddressingMode.Immediate)],
                condition: P2ConditionCode.IF_NC),
            new AsmLabelNode(loopBody),
            new AsmInstructionNode(P2Mnemonic.MOV, [temp, new AsmImmediateOperand(0)]),
            new AsmInstructionNode(P2Mnemonic.GETNIB, [temp, carried, new AsmImmediateOperand(7)]),
            new AsmInstructionNode(P2Mnemonic.MOV, [updated, index]),
            new AsmInstructionNode(P2Mnemonic.ADD, [updated, new AsmImmediateOperand(1)]),
            new AsmInstructionNode(P2Mnemonic.MOV, [index, updated]),
            new AsmInstructionNode(
                P2Mnemonic.JMP,
                [new AsmSymbolOperand(loopHeader, AsmSymbolAddressingMode.Immediate)]),
            new AsmLabelNode(done),
            new AsmInstructionNode(P2Mnemonic.RET, []),
        ]);

        FunctionLiveness liveness = LivenessAnalyzer.Analyze(function);

        Assert.That(
            liveness.Blocks.Any(block => block.Defs.Contains(temp.Value) && block.LiveOut.Contains(limit.Value)),
            Is.True);
        Assert.That(liveness.InterferenceGraph.ContainsKey(limit.Value), Is.True);
        Assert.That(liveness.InterferenceGraph[limit.Value].Contains(temp.Value), Is.True);
    }

    [Test]
    public void CodegenPipeline_DoesNotCollapseCompareSourcesIntoSameRegister()
    {
        AsmRegisterOperand limit = AsmRegister(1);
        AsmRegisterOperand index = AsmRegister(2);
        AsmRegisterOperand sink = AsmRegister(3);
        ControlFlowLabelSymbol done = new("f_done");

        AsmModule asmModule = CreateAsmModule(functions:
        [
            CreateAsmFunction("f", isEntryPoint: false, CallingConventionTier.General,
            [
                new AsmInstructionNode(P2Mnemonic.MOV, [limit, new AsmImmediateOperand(8)]),
                new AsmInstructionNode(P2Mnemonic.MOV, [index, new AsmImmediateOperand(0)]),
                new AsmInstructionNode(
                    P2Mnemonic.CMP,
                    [index, limit],
                    flagOutput: new AsmFlagOutput(P2FlagEffect.WC, null, null)),
                new AsmInstructionNode(
                    P2Mnemonic.JMP,
                    [new AsmSymbolOperand(done, AsmSymbolAddressingMode.Immediate)],
                    condition: P2ConditionCode.IF_NC),
                new AsmInstructionNode(P2Mnemonic.MOV, [sink, index]),
                new AsmLabelNode(done),
                new AsmInstructionNode(P2Mnemonic.RET, []),
            ]),
        ]);

        IrBuildResult emit = CreateBuildResult(asmModule);
        CodegenPipeline.Emit(emit, new EmitOptions
        {
            EnabledAsmirOptimizations = [],
        });

        Assert.That(emit.AssemblyText, Does.Match(@"CMP\s+[A-Za-z_]\w*,\s+[A-Za-z_]\w*\s+WC\b"));
        Assert.That(emit.AssemblyText, Does.Not.Match(@"CMP\s+([A-Za-z_]\w*),\s+\1\s+WC\b"), emit.AssemblyText);
    }

    [Test]
    public void CodegenPipeline_DoesNotReuseLoopBoundRegisterAsBodyScratch()
    {
        AsmRegisterOperand limit = AsmRegister(1);
        AsmRegisterOperand index = AsmRegister(2);
        AsmRegisterOperand carried = AsmRegister(3);
        AsmRegisterOperand temp = AsmRegister(4);
        AsmRegisterOperand updated = AsmRegister(5);
        ControlFlowLabelSymbol loopHeader = new("f_loop_header");
        ControlFlowLabelSymbol loopBody = new("f_loop_body");
        ControlFlowLabelSymbol done = new("f_done");

        AsmModule asmModule = CreateAsmModule(functions:
        [
            CreateAsmFunction("f", isEntryPoint: false, CallingConventionTier.General,
            [
                new AsmInstructionNode(P2Mnemonic.MOV, [limit, new AsmImmediateOperand(8)]),
                new AsmInstructionNode(P2Mnemonic.MOV, [index, new AsmImmediateOperand(0)]),
                new AsmInstructionNode(P2Mnemonic.MOV, [carried, new AsmImmediateOperand(1)]),
                new AsmLabelNode(loopHeader),
                new AsmInstructionNode(
                    P2Mnemonic.CMP,
                    [index, limit],
                    flagOutput: new AsmFlagOutput(P2FlagEffect.WC, null, null)),
                new AsmInstructionNode(
                    P2Mnemonic.JMP,
                    [new AsmSymbolOperand(done, AsmSymbolAddressingMode.Immediate)],
                    condition: P2ConditionCode.IF_NC),
                new AsmLabelNode(loopBody),
                new AsmInstructionNode(P2Mnemonic.MOV, [temp, new AsmImmediateOperand(0)]),
                new AsmInstructionNode(P2Mnemonic.GETNIB, [temp, carried, new AsmImmediateOperand(7)]),
                new AsmInstructionNode(P2Mnemonic.MOV, [updated, index]),
                new AsmInstructionNode(P2Mnemonic.ADD, [updated, new AsmImmediateOperand(1)]),
                new AsmInstructionNode(P2Mnemonic.MOV, [index, updated]),
                new AsmInstructionNode(
                    P2Mnemonic.JMP,
                    [new AsmSymbolOperand(loopHeader, AsmSymbolAddressingMode.Immediate)]),
                new AsmLabelNode(done),
                new AsmInstructionNode(P2Mnemonic.RET, []),
            ]),
        ]);

        IrBuildResult emit = CreateBuildResult(asmModule);
        CodegenPipeline.Emit(emit, new EmitOptions
        {
            EnabledAsmirOptimizations = [],
        });

        Match compare = Regex.Match(emit.AssemblyText, @"CMP\s+[A-Za-z_]\w*,\s+([A-Za-z_]\w*)\s+WC\b");
        Assert.That(compare.Success, Is.True, emit.AssemblyText);

        string limitRegister = compare.Groups[1].Value;
        Assert.That(emit.AssemblyText, Does.Not.Contain($"MOV {limitRegister}, #0"), emit.AssemblyText);
        Assert.That(emit.AssemblyText, Does.Not.Contain($"GETNIB {limitRegister},"), emit.AssemblyText);
    }

    private static IrBuildResult CreateBuildResult(AsmModule asmModule)
    {
        ImagePlan imagePlan = IrTestFactory.CreateImagePlanFromModule(asmModule);
        BoundProgram program = IrTestFactory.CreateBoundProgram("/tmp/test.blade");
        ImagePlacement imagePlacement = ImagePlacer.Place(imagePlan);
        LayoutSolution layoutSolution = LayoutSolver.SolveStableLayouts(program, imagePlacement);
        CogResourceLayoutSet cogResourceLayouts = IrTestFactory.CreateSimpleCogResourceLayouts(asmModule, imagePlan, includeDefaultBladeHalt: false);
        MirModule mirModule = CreateMirModule();
        LirModule lirModule = CreateLirModule();
        return new IrBuildResult(imagePlan, imagePlacement, layoutSolution, cogResourceLayouts, mirModule, mirModule, lirModule, lirModule, asmModule, asmModule, asmModule, string.Empty);
    }
}
