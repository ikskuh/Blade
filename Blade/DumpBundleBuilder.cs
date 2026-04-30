using System.Collections.Generic;
using Blade.IR;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics.Bound;

namespace Blade;

/// <summary>
/// Builds the ordered dump artifact bundle for one compilation result.
/// The returned artifact list is the canonical dump model used by every output surface.
/// </summary>
public static class DumpBundleBuilder
{
    /// <summary>
    /// Materializes the selected dumps for one IR build result.
    /// </summary>
    public static IReadOnlyList<DumpArtifact> Build(DumpSelection selection, IrBuildResult buildResult)
    {
        Requires.NotNull(selection);
        Requires.NotNull(buildResult);

        if (!HasExplicitSelection(selection))
            return [CreateFinalAssemblyArtifact(buildResult.AssemblyText)];

        List<DumpArtifact> artifacts = [];
        if (selection.DumpBound)
        {
            artifacts.Add(CreateArtifact("bound", "Bound", "00_bound.ir", BoundTreeWriter.Write(buildResult.BoundProgram)));
            artifacts.Add(CreateArtifact("images", "Images", "02_images.ir", ImagePlanDumpWriter.Write(buildResult.ImagePlan)));
            artifacts.Add(CreateArtifact("layout-solution", "Layout Solution", "03_layout_solution.ir", LayoutSolutionDumpWriter.Write(buildResult.LayoutSolution)));
        }

        if (selection.DumpMirPreOptimization)
            artifacts.Add(CreateArtifact("mir-preopt", "MIR (Preopt)", "05_mir_preopt.ir", MirTextWriter.Write(buildResult.PreOptimizationMirModules)));
        if (selection.DumpMir)
            artifacts.Add(CreateArtifact("mir", "MIR", "10_mir.ir", MirTextWriter.Write(buildResult.MirModules)));
        if (selection.DumpLirPreOptimization)
            artifacts.Add(CreateArtifact("lir-preopt", "LIR (Preopt)", "15_lir_preopt.ir", LirTextWriter.Write(buildResult.PreOptimizationLirModules)));
        if (selection.DumpLir)
            artifacts.Add(CreateArtifact("lir", "LIR", "20_lir.ir", LirTextWriter.Write(buildResult.LirModules)));
        if (selection.DumpAsmirPreOptimization)
            artifacts.Add(CreateArtifact("asmir-preopt", "ASMIR (Preopt)", "25_asmir_preopt.ir", AsmTextWriter.Write(buildResult.PreOptimizationAsmModules)));
        if (selection.DumpAsmir)
            artifacts.Add(CreateArtifact("asmir", "ASMIR", "30_asmir.ir", AsmTextWriter.Write(buildResult.AsmModules)));
        if (selection.DumpMemoryMap)
            artifacts.Add(CreateArtifact("image-memory-maps", "Image Memory Maps", "35_image_memory_maps.ir", ImageMemoryMapDumpWriter.Write(buildResult)));
        if (selection.DumpFinalAsm)
            artifacts.Add(CreateFinalAssemblyArtifact(buildResult.AssemblyText));

        return artifacts;
    }

    private static bool HasExplicitSelection(DumpSelection selection)
    {
        return selection.DumpBound
            || selection.DumpMirPreOptimization
            || selection.DumpMir
            || selection.DumpLirPreOptimization
            || selection.DumpLir
            || selection.DumpAsmirPreOptimization
            || selection.DumpAsmir
            || selection.DumpMemoryMap
            || selection.DumpFinalAsm;
    }

    private static DumpArtifact CreateFinalAssemblyArtifact(string assemblyText)
    {
        return CreateArtifact("final-asm", "Final Assembly", "40_final.spin2", assemblyText);
    }

    private static DumpArtifact CreateArtifact(string id, string title, string fileName, string content)
    {
        return new DumpArtifact(id, title, fileName, content);
    }
}

internal static class DumpSelectionFactory
{
    public static DumpSelection FromCommandLineOptions(CommandLineOptions options)
    {
        Requires.NotNull(options);

        return new DumpSelection
        {
            DumpBound = options.DumpBound,
            DumpMirPreOptimization = options.DumpMirPreOptimization,
            DumpMir = options.DumpMir,
            DumpLirPreOptimization = options.DumpLirPreOptimization,
            DumpLir = options.DumpLir,
            DumpAsmirPreOptimization = options.DumpAsmirPreOptimization,
            DumpAsmir = options.DumpAsmir,
            DumpMemoryMap = options.DumpMemoryMap,
            DumpFinalAsm = options.DumpFinalAsm,
        };
    }
}
