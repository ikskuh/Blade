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
        MirModule mirModule = MirLowerer.Lower(boundProgram, layoutSolution);

        bool enableSingleCallsiteInlining = options.EnableSingleCallsiteInlining
            && options.EnabledMirOptimizations.Contains(OptimizationRegistry.SingleCallsiteInlineMirOptimization);

        mirModule = MirInliner.InlineMandatoryAndSingleCallsite(
            mirModule,
            enableSingleCallsiteInlining);
        MirModule preOptimizationMirModule = mirModule;

        if (options.EnableMirOptimizations)
        {
            mirModule = MirOptimizer.Optimize(
                mirModule,
                options.MaxOptimizationIterations,
                options.EnabledMirOptimizations);
        }

        LirModule lirModule = LirLowerer.Lower(mirModule);
        LirModule preOptimizationLirModule = lirModule;
        if (options.EnableLirOptimizations)
        {
            lirModule = LirOptimizer.Optimize(
                lirModule,
                options.MaxOptimizationIterations,
                options.EnabledLirOptimizations);
        }

        AsmModule asmModule = AsmLowerer.Lower(lirModule, imagePlan, diagnostics);
        AsmModule preOptimizationAsmModule = asmModule;

        CogResourceLayout placeholderEntryLayout = new(imagePlan.EntryImage, 0, [], []);
        CogResourceLayoutSet placeholderCogResourceLayouts = new(
            [placeholderEntryLayout],
            placeholderEntryLayout,
            new Dictionary<IAsmSymbol, int>(),
            new Dictionary<FunctionSymbol, CogResourceLayout>(),
            new Dictionary<StoragePlace, CogResourceLayout>(),
            0);

        IrBuildResult preEmit = new(
            boundProgram,
            imagePlan,
            imagePlacement,
            layoutSolution,
            placeholderCogResourceLayouts,
            preOptimizationMirModule,
            mirModule,
            preOptimizationLirModule,
            lirModule,
            preOptimizationAsmModule,
            asmModule,
            assemblyText: string.Empty);
        EmitResult emitResult = CodegenPipeline.Emit(preEmit, new EmitOptions
        {
            EnabledAsmirOptimizations = options.EnabledAsmirOptimizations,
            RuntimeTemplate = options.RuntimeTemplate,
        }, diagnostics);
        return new IrBuildResult(
            boundProgram,
            imagePlan,
            imagePlacement,
            layoutSolution,
            emitResult.CogResourceLayouts,
            preOptimizationMirModule,
            mirModule,
            preOptimizationLirModule,
            lirModule,
            preOptimizationAsmModule,
            emitResult.AsmModule,
            emitResult.AssemblyText);
    }
}
