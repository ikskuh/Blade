using System.Collections.Generic;
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

        EmitResult emit = CodegenPipeline.Emit(CreateBuildResult(asmModule), new EmitOptions
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

        EmitResult emit = CodegenPipeline.Emit(CreateBuildResult(asmModule), new EmitOptions
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
            new AsmInstructionNode(P2Mnemonic.MOV, [dstA, srcA], P2ConditionCode.IF_Z, isPhiMove: true),
            new AsmInstructionNode(P2Mnemonic.MOV, [dstB, srcB], P2ConditionCode.IF_Z, isPhiMove: true),
            new AsmInstructionNode(P2Mnemonic.JMP, [new AsmSymbolOperand(done, AsmSymbolAddressingMode.Immediate)], P2ConditionCode.IF_Z),
            new AsmLabelNode(done),
        ]);

        FunctionLiveness liveness = LivenessAnalyzer.Analyze(function);

        Assert.That(liveness.InterferenceGraph.ContainsKey(srcA.Register), Is.True);
        Assert.That(liveness.InterferenceGraph[srcA.Register].Contains(srcB.Register), Is.True);
    }

    private static IrBuildResult CreateBuildResult(AsmModule asmModule)
    {
        BoundProgram program = IrTestFactory.CreateBoundProgram("/tmp/test.blade");
        ImagePlan imagePlan = IrTestFactory.CreateImagePlanFromModule(asmModule);
        ImagePlacement imagePlacement = ImagePlacer.Place(imagePlan);
        LayoutSolution layoutSolution = LayoutSolver.SolveStableLayouts(program, imagePlacement);
        CogResourceLayoutSet cogResourceLayouts = IrTestFactory.CreateSimpleCogResourceLayouts(asmModule, imagePlan, includeDefaultBladeHalt: false);
        MirModule mirModule = CreateMirModule();
        LirModule lirModule = CreateLirModule();
        return new IrBuildResult(program, imagePlan, imagePlacement, layoutSolution, cogResourceLayouts, mirModule, mirModule, lirModule, lirModule, asmModule, asmModule, string.Empty);
    }
}
