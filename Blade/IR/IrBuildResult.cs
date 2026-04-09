using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics.Bound;

namespace Blade.IR;

public sealed class IrBuildResult
{
    public IrBuildResult(
        BoundModule boundModule,
        MirModule preOptimizationMirModule,
        MirModule mirModule,
        LirModule preOptimizationLirModule,
        LirModule lirModule,
        AsmModule preOptimizationAsmModule,
        AsmModule asmModule,
        string assemblyText)
    {
        BoundModule = boundModule;
        PreOptimizationMirModule = preOptimizationMirModule;
        MirModule = mirModule;
        PreOptimizationLirModule = preOptimizationLirModule;
        LirModule = lirModule;
        PreOptimizationAsmModule = preOptimizationAsmModule;
        AsmModule = asmModule;
        AssemblyText = assemblyText;
    }

    public BoundModule BoundModule { get; }
    public MirModule PreOptimizationMirModule { get; }
    public MirModule MirModule { get; }
    public LirModule PreOptimizationLirModule { get; }
    public LirModule LirModule { get; }
    public AsmModule PreOptimizationAsmModule { get; }
    public AsmModule AsmModule { get; }
    public string AssemblyText { get; }
}

public sealed class EmitResult
{
    public EmitResult(AsmModule asmModule, string assemblyText)
    {
        AsmModule = asmModule;
        AssemblyText = assemblyText;
    }

    public AsmModule AsmModule { get; }
    public string AssemblyText { get; }
}
