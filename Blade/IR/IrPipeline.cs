using System.Linq;
using Blade.Diagnostics;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics.Bound;

namespace Blade.IR;

public static class IrPipeline
{
    public static IrBuildResult Build(BoundProgram boundProgram, IrPipelineOptions? options = null, DiagnosticBag? diagnostics = null)
    {
        options ??= new IrPipelineOptions();

        ImagePlan imagePlan = ImagePlanner.Build(boundProgram);
        ImagePlacement imagePlacement = ImagePlacer.Place(imagePlan);
        LayoutSolution layoutSolution = LayoutSolver.Solve(boundProgram, imagePlacement, diagnostics);
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

        AsmModule asmModule = AsmLowerer.Lower(lirModule, diagnostics);
        AsmModule preOptimizationAsmModule = asmModule;

        IrBuildResult preEmit = new(
            boundProgram,
            imagePlan,
            imagePlacement,
            layoutSolution,
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
        });
        return new IrBuildResult(
            boundProgram,
            imagePlan,
            imagePlacement,
            layoutSolution,
            preOptimizationMirModule,
            mirModule,
            preOptimizationLirModule,
            lirModule,
            preOptimizationAsmModule,
            emitResult.AsmModule,
            emitResult.AssemblyText);
    }
}
