using System.Collections.Generic;
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
    IReadOnlyList<MirModule> preOptimizationMirModules,
    IReadOnlyList<MirModule> mirModules,
    IReadOnlyList<LirModule> preOptimizationLirModules,
    IReadOnlyList<LirModule> lirModules,
    IReadOnlyList<AsmModule> preOptimizationAsmModules,
    IReadOnlyList<AsmModule> asmModules,
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
    public IReadOnlyList<MirModule> PreOptimizationMirModules { get; } = preOptimizationMirModules;
    public IReadOnlyList<MirModule> MirModules { get; } = mirModules;
    public IReadOnlyList<LirModule> PreOptimizationLirModules { get; } = preOptimizationLirModules;
    public IReadOnlyList<LirModule> LirModules { get; } = lirModules;
    public IReadOnlyList<AsmModule> PreOptimizationAsmModules { get; } = preOptimizationAsmModules;
    public IReadOnlyList<AsmModule> AsmModules { get; } = asmModules;
    public string AssemblyText { get; } = assemblyText;
}

public sealed class EmitResult(IReadOnlyList<AsmModule> asmModules, CogResourceLayoutSet cogResourceLayouts, string assemblyText)
{
    public IReadOnlyList<AsmModule> AsmModules { get; } = asmModules;
    public CogResourceLayoutSet CogResourceLayouts { get; } = cogResourceLayouts;
    public string AssemblyText { get; } = assemblyText;
}
