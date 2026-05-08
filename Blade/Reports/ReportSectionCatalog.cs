using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Blade.IR;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics.Bound;

namespace Blade.Reports;

/// <summary>
/// Represents one renderable report section.
/// </summary>
public sealed class ReportSection(string id, string title, string fileName, Action<ITextReportBuilder> emit)
{
    /// <summary>
    /// Gets the stable section identifier.
    /// </summary>
    public string Id { get; } = Requires.NotNullOrWhiteSpace(id);

    /// <summary>
    /// Gets the user-facing section title.
    /// </summary>
    public string Title { get; } = Requires.NotNullOrWhiteSpace(title);

    /// <summary>
    /// Gets the canonical filename associated with this section.
    /// </summary>
    public string FileName { get; } = Requires.NotNullOrWhiteSpace(fileName);

    /// <summary>
    /// Gets the rendered textual content for this section.
    /// </summary>
    public Action<ITextReportBuilder> Emit { get; } = Requires.NotNull(emit);

    /// <summary>
    /// Renders this section as plain text.
    /// </summary>
    public string RenderPlainText()
    {
        StringBuilder sb = new();
        Emit(new PlainTextReportBuilder(sb));
        return sb.ToString();
    }
}

/// <summary>
/// Builds the available renderable sections for a compilation output.
/// </summary>
public static class ReportSectionCatalog
{
    /// <summary>
    /// Builds the renderable sections that are available for the supplied compilation output.
    /// </summary>
    public static IReadOnlyList<ReportSection> BuildSections(CompilationOutput output)
    {
        Requires.NotNull(output);

        List<ReportSection> sections = [];
        AddBoundSections(sections, output.BoundProgram, output.Stages);
        AddMirSection(sections, "mir-preopt", "MIR (Preopt)", "05_mir_preopt.ir", output.Stages.PreOptimizationMirModules);
        AddMirSection(sections, "mir", "MIR", "10_mir.ir", output.Stages.MirModules);
        AddLirSection(sections, "lir-preopt", "LIR (Preopt)", "15_lir_preopt.ir", output.Stages.PreOptimizationLirModules);
        AddLirSection(sections, "lir", "LIR", "20_lir.ir", output.Stages.LirModules);
        AddAsmSection(sections, "asmir-preopt", "ASMIR (Preopt)", "25_asmir_preopt.ir", output.Stages.PreOptimizationAsmModules);
        AddAsmSection(sections, "asmir", "ASMIR", "30_asmir.ir", output.Stages.AsmModules);
        AddMemoryMapSection(sections, output.Stages);
        return sections;
    }

    private static void AddBoundSections(List<ReportSection> sections, BoundProgram? boundProgram, Blade.IR.CompilationStageOutput stages)
    {
        if (boundProgram is null)
            return;

        sections.Add(new ReportSection("bound", "Bound", "00_bound.ir", writer => BoundTreeWriter.Write(writer, boundProgram)));
        if (stages.ImagePlan is not null)
            sections.Add(new ReportSection("images", "Images", "02_images.ir", writer => ImagePlanDumpWriter.Write(writer, stages.ImagePlan)));
        if (stages.LayoutSolution is not null)
            sections.Add(new ReportSection("layout-solution", "Layout Solution", "03_layout_solution.ir", writer => LayoutSolutionDumpWriter.Write(writer, stages.LayoutSolution)));
    }

    private static void AddMirSection(List<ReportSection> sections, string id, string title, string fileName, IReadOnlyList<MirModule>? modules)
    {
        if (modules is null)
            return;

        sections.Add(new ReportSection(id, title, fileName, writer => MirTextWriter.Write(writer, modules)));
    }

    private static void AddLirSection(List<ReportSection> sections, string id, string title, string fileName, IReadOnlyList<LirModule>? modules)
    {
        if (modules is null)
            return;

        sections.Add(new ReportSection(id, title, fileName, writer => LirTextWriter.Write(writer, modules)));
    }

    private static void AddAsmSection(List<ReportSection> sections, string id, string title, string fileName, IReadOnlyList<AsmModule>? modules)
    {
        if (modules is null)
            return;

        sections.Add(new ReportSection(id, title, fileName, writer => AsmTextWriter.Write(writer, modules)));
    }

    private static void AddMemoryMapSection(List<ReportSection> sections, Blade.IR.CompilationStageOutput stages)
    {
        if (stages.ImagePlan is null
            || stages.ImagePlacement is null
            || stages.LayoutSolution is null
            || stages.CogResourceLayouts is null
            || stages.MirModules is null
            || stages.AsmModules is null)
        {
            return;
        }

        foreach (ImageDescriptor image in stages.ImagePlacement.Images.Select(static entry => entry.Image))
        {
            if (!stages.CogResourceLayouts.Images.Any(layout => ReferenceEquals(layout.Image, image))
                || !stages.AsmModules.Any(module => ReferenceEquals(module.Image, image)))
            {
                return;
            }
        }

        sections.Add(new ReportSection("image-memory-maps", "Image Memory Maps", "35_image_memory_maps.ir", writer => ImageMemoryMapDumpWriter.Write(writer, stages)));
    }

}
