namespace Blade;

/// <summary>
/// Selects which compiler dump stages should be materialized into dump artifacts.
/// When no flags are enabled, dump generation falls back to a single final-assembly
/// artifact so the compiler still has one canonical rendered result.
/// </summary>
public sealed class DumpSelection
{
    /// <summary>
    /// Gets or sets a value indicating whether the bound-stage dumps should be emitted.
    /// This includes the bound tree, the image plan, and the layout solution.
    /// </summary>
    public bool DumpBound { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the pre-optimization MIR dump should be emitted.
    /// </summary>
    public bool DumpMirPreOptimization { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the optimized MIR dump should be emitted.
    /// </summary>
    public bool DumpMir { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the pre-optimization LIR dump should be emitted.
    /// </summary>
    public bool DumpLirPreOptimization { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the optimized LIR dump should be emitted.
    /// </summary>
    public bool DumpLir { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the pre-optimization ASMIR dump should be emitted.
    /// </summary>
    public bool DumpAsmirPreOptimization { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the optimized ASMIR dump should be emitted.
    /// </summary>
    public bool DumpAsmir { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the image memory-map dump should be emitted.
    /// </summary>
    public bool DumpMemoryMap { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the final assembly dump should be emitted.
    /// </summary>
    public bool DumpFinalAsm { get; init; }
}
