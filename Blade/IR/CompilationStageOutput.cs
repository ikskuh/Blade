using System.Collections.Generic;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;

namespace Blade.IR;

/// <summary>
/// Carries the partially or fully produced backend stages for a compilation.
/// </summary>
public sealed class CompilationStageOutput
{
    /// <summary>
    /// Gets or sets the planned execution images.
    /// </summary>
    public ImagePlan? ImagePlan { get; set; }

    /// <summary>
    /// Gets or sets the placed hub-memory images.
    /// </summary>
    public ImagePlacement? ImagePlacement { get; set; }

    /// <summary>
    /// Gets or sets the solved storage layout.
    /// </summary>
    public LayoutSolution? LayoutSolution { get; set; }

    /// <summary>
    /// Gets or sets the stable COG resource layout solution.
    /// </summary>
    public CogResourceLayoutSet? CogResourceLayouts { get; set; }

    /// <summary>
    /// Gets or sets the MIR modules before optimization.
    /// </summary>
    public IReadOnlyList<MirModule>? PreOptimizationMirModules { get; set; }

    /// <summary>
    /// Gets or sets the optimized MIR modules.
    /// </summary>
    public IReadOnlyList<MirModule>? MirModules { get; set; }

    /// <summary>
    /// Gets or sets the LIR modules before optimization.
    /// </summary>
    public IReadOnlyList<LirModule>? PreOptimizationLirModules { get; set; }

    /// <summary>
    /// Gets or sets the optimized LIR modules.
    /// </summary>
    public IReadOnlyList<LirModule>? LirModules { get; set; }

    /// <summary>
    /// Gets or sets the ASMIR modules before optimization.
    /// </summary>
    public IReadOnlyList<AsmModule>? PreOptimizationAsmModules { get; set; }

    /// <summary>
    /// Gets or sets the optimized ASMIR modules.
    /// </summary>
    public IReadOnlyList<AsmModule>? AsmModules { get; set; }

    /// <summary>
    /// Gets or sets the emitted final assembly text.
    /// </summary>
    public string? AssemblyText { get; set; }
}
