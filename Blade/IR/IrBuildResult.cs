using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics.Bound;

namespace Blade.IR;

public sealed class IrBuildResult
{
    public IrBuildResult(
        BoundProgram boundProgram,
        MirModule mirModule,
        LirModule lirModule,
        AsmModule asmModule,
        string assemblyText)
    {
        BoundProgram = boundProgram;
        MirModule = mirModule;
        LirModule = lirModule;
        AsmModule = asmModule;
        AssemblyText = assemblyText;
    }

    public BoundProgram BoundProgram { get; }
    public MirModule MirModule { get; }
    public LirModule LirModule { get; }
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
