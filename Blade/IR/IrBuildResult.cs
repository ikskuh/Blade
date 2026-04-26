using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics.Bound;

namespace Blade.IR;

public sealed class IrBuildResult(
    BoundProgram boundProgram,
    ImagePlan imagePlan,
    ImagePlacement imagePlacement,
    LayoutSolution layoutSolution,
    CogResourceLayoutSet cogResourceLayouts,
    MirModule preOptimizationMirModule,
    MirModule mirModule,
    LirModule preOptimizationLirModule,
    LirModule lirModule,
    AsmModule preOptimizationAsmModule,
    AsmModule asmModule,
    string assemblyText)
{
    public BoundProgram BoundProgram { get; } = boundProgram;
    public ImagePlan ImagePlan { get; } = imagePlan;

    /// <summary>
    /// Gets the concrete hub-memory placement of the required images.
    /// </summary>
    public ImagePlacement ImagePlacement { get; } = imagePlacement;

    /// <summary>
    /// Gets the program-wide solved addresses for layout-backed storage.
    /// </summary>
    public LayoutSolution LayoutSolution { get; } = layoutSolution;

    /// <summary>
    /// Gets the stable COG-backed data addresses and per-image allocatable register pools.
    /// </summary>
    public CogResourceLayoutSet CogResourceLayouts { get; } = cogResourceLayouts;
    public MirModule PreOptimizationMirModule { get; } = preOptimizationMirModule;
    public MirModule MirModule { get; } = mirModule;
    public LirModule PreOptimizationLirModule { get; } = preOptimizationLirModule;
    public LirModule LirModule { get; } = lirModule;
    public AsmModule PreOptimizationAsmModule { get; } = preOptimizationAsmModule;
    public AsmModule AsmModule { get; } = asmModule;
    public string AssemblyText { get; } = assemblyText;
}

public sealed class EmitResult(AsmModule asmModule, CogResourceLayoutSet cogResourceLayouts, string assemblyText)
{
    public AsmModule AsmModule { get; } = asmModule;
    public CogResourceLayoutSet CogResourceLayouts { get; } = cogResourceLayouts;
    public string AssemblyText { get; } = assemblyText;
}
