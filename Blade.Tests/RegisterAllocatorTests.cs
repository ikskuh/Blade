using System.Collections.Generic;
using Blade.IR;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Semantics.Bound;

namespace Blade.Tests;

[TestFixture]
public class RegisterAllocatorTests
{
    [Test]
    public void CodegenPipeline_RemovesSelfMovesIntroducedByRegisterAllocation()
    {
        AsmRegisterOperand source = new(1);
        AsmRegisterOperand copy = new(2);

        AsmModule asmModule = new([
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

        Assert.That(emit.AssemblyText, Does.Not.Match(@"MOV\s+(_r\d+),\s+\1\b"), emit.AssemblyText);
        Assert.That(emit.AssemblyText, Does.Match(@"MOV\s+OUTA,\s+_r\d+\b"), emit.AssemblyText);
    }

    [Test]
    public void CodegenPipeline_LeavesAllocatorSelfMovesWhenCleanupSelfMovDisabled()
    {
        AsmRegisterOperand source = new(1);
        AsmRegisterOperand copy = new(2);

        AsmModule asmModule = new([
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

        Assert.That(emit.AssemblyText, Does.Match(@"MOV\s+(_r\d+),\s+\1\b"), emit.AssemblyText);
    }

    private static IrBuildResult CreateBuildResult(AsmModule asmModule)
    {
        BoundProgram program = new([], [], [], new Dictionary<string, TypeSymbol>(), new Dictionary<string, FunctionSymbol>(), new Dictionary<string, ImportedModule>());
        MirModule mirModule = new([]);
        LirModule lirModule = new([]);
        return new IrBuildResult(program, mirModule, mirModule, lirModule, lirModule, asmModule, asmModule, string.Empty);
    }
}
