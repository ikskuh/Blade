using System.Collections.Generic;
using System.Linq;
using Blade.IR.Asm;
using Blade.Diagnostics;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Semantics.Bound;

namespace Blade.IR;

public static class IrPipeline
{
    public static IrBuildResult Build(BoundProgram boundProgram, IrPipelineOptions? options = null, DiagnosticBag? diagnostics = null)
    {
        options ??= new IrPipelineOptions();

        ImagePlan imagePlan = ImagePlanner.Build(boundProgram);
        ImagePlacement imagePlacement = ImagePlacer.Place(imagePlan);
        LayoutSolution layoutSolution = LayoutSolver.SolveStableLayouts(boundProgram, imagePlacement, diagnostics);
        List<MirModule> mirModules = MirLowerer.Lower(boundProgram, imagePlan, layoutSolution).ToList();

        bool enableSingleCallsiteInlining = options.EnableSingleCallsiteInlining
            && options.EnabledMirOptimizations.Contains(OptimizationRegistry.SingleCallsiteInlineMirOptimization);

        for (int i = 0; i < mirModules.Count; i++)
        {
            mirModules[i] = MirInliner.InlineMandatoryAndSingleCallsite(
                mirModules[i],
                enableSingleCallsiteInlining);
        }
        IReadOnlyList<MirModule> preOptimizationMirModules = mirModules.ToList();

        if (options.EnableMirOptimizations)
        {
            for (int i = 0; i < mirModules.Count; i++)
            {
                mirModules[i] = MirOptimizer.Optimize(
                    mirModules[i],
                    options.MaxOptimizationIterations,
                    options.EnabledMirOptimizations);
            }
        }

        List<LirModule> lirModules = mirModules.Select(LirLowerer.Lower).ToList();
        IReadOnlyList<LirModule> preOptimizationLirModules = lirModules.ToList();
        if (options.EnableLirOptimizations)
        {
            for (int i = 0; i < lirModules.Count; i++)
            {
                lirModules[i] = LirOptimizer.Optimize(
                    lirModules[i],
                    options.MaxOptimizationIterations,
                    options.EnabledLirOptimizations);
            }
        }

        List<AsmModule> asmModules = lirModules
            .Select(module => AsmLowerer.Lower(module, imagePlan, diagnostics))
            .ToList();
        IReadOnlyList<AsmModule> preOptimizationAsmModules = asmModules.ToList();

        CogResourceLayout placeholderEntryLayout = new(imagePlacement.EntryImage, 0, [], []);
        CogResourceLayoutSet placeholderCogResourceLayouts = new(
            [placeholderEntryLayout],
            placeholderEntryLayout,
            new Dictionary<IAsmSymbol, MemoryAddress>(),
            new Dictionary<ImageDescriptor, CogResourceLayout>(),
            new Dictionary<StoragePlace, CogResourceLayout>(),
            0);

        IrBuildResult preEmit = new(
            boundProgram,
            imagePlan,
            imagePlacement,
            layoutSolution,
            placeholderCogResourceLayouts,
            preOptimizationMirModules,
            mirModules,
            preOptimizationLirModules,
            lirModules,
            preOptimizationAsmModules,
            asmModules,
            assemblyText: string.Empty);
        EmitResult emitResult = CodegenPipeline.Emit(preEmit, new EmitOptions
        {
            EnabledAsmirOptimizations = options.EnabledAsmirOptimizations,
        }, diagnostics);
        return new IrBuildResult(
            boundProgram,
            imagePlan,
            imagePlacement,
            layoutSolution,
            emitResult.CogResourceLayouts,
            preOptimizationMirModules,
            mirModules,
            preOptimizationLirModules,
            lirModules,
            preOptimizationAsmModules,
            emitResult.AsmModules,
            emitResult.AssemblyText);
    }
}
