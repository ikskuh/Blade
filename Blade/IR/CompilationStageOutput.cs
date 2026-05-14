using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Reports;

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
    /// Gets or sets the ASMIR modules after optimization and legalization, but before register allocation.
    /// </summary>
    public IReadOnlyList<AsmModule>? PreRegisterAllocationAsmModules { get; set; }

    public bool IsComplete => this.AsmModules is not null
                           && this.CogResourceLayouts is not null
                           ;

    /// <summary>
    /// Renders the compilation output into the final assembly text.
    /// </summary>
    /// <returns></returns>
    public string RenderAssemblyText()
    {
        using var sw = new StringWriter();
        this.RenderAssemblyText(sw);
        return sw.ToString();
    }

    /// <summary>
    /// Renders the compilation output into the final assembly text.
    /// </summary>
    /// <returns></returns>
    public void RenderAssemblyText(TextWriter writer)
    {
        Requires.NotNull(writer);
        this.RenderAssemblyText(new PlainTextReportBuilder(writer));
        writer.Flush();
    }

    public void RenderAssemblyText(ITextReportBuilder writer)
    {
        Requires.NotNull(writer);
        if (this.AsmModules == null || this.CogResourceLayouts == null)
            throw new InvalidOperationException("Can only render the final assembly text when the output is complete.");

        FinalAssemblyWriter.Write(writer, this.AsmModules, this.CogResourceLayouts);
    }

}
