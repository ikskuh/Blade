using System;
using System.Collections.Generic;
using System.Linq;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics.Bound;

namespace Blade.IR;

public static class IrPipeline
{
    public static IrBuildResult Build(BoundProgram boundProgram, IrPipelineOptions? options = null)
    {
        options ??= new IrPipelineOptions();

        MirModule mirModule = MirLowerer.Lower(boundProgram);

        IReadOnlyList<string> enabledMirOptimizations = OptimizationCatalog.ResolveEnabled(
            OptimizationStage.Mir,
            options.OptimizationDirectives);
        bool enableSingleCallsiteInlining = options.EnableSingleCallsiteInlining
            && enabledMirOptimizations.Contains("single-callsite-inline", StringComparer.Ordinal);

        mirModule = MirInliner.InlineMandatoryAndSingleCallsite(
            mirModule,
            enableSingleCallsiteInlining);
        MirModule preOptimizationMirModule = mirModule;

        if (options.EnableMirOptimizations)
        {
            mirModule = MirOptimizer.Optimize(
                mirModule,
                options.MaxOptimizationIterations,
                enabledMirOptimizations);
        }

        LirModule lirModule = LirLowerer.Lower(mirModule);
        LirModule preOptimizationLirModule = lirModule;
        if (options.EnableLirOptimizations)
            lirModule = LirOptimizer.Optimize(
                lirModule,
                options.MaxOptimizationIterations,
                OptimizationCatalog.ResolveEnabled(OptimizationStage.Lir, options.OptimizationDirectives));

        AsmModule asmModule = AsmLowerer.Lower(lirModule);
        AsmModule preOptimizationAsmModule = asmModule;

        IrBuildResult preEmit = new(
            boundProgram,
            preOptimizationMirModule,
            mirModule,
            preOptimizationLirModule,
            lirModule,
            preOptimizationAsmModule,
            asmModule,
            assemblyText: string.Empty);
        EmitResult emitResult = CodegenPipeline.Emit(preEmit, new EmitOptions
        {
            EnabledAsmirOptimizations = OptimizationCatalog.ResolveEnabled(OptimizationStage.Asmir, options.OptimizationDirectives),
        });
        return new IrBuildResult(
            boundProgram,
            preOptimizationMirModule,
            mirModule,
            preOptimizationLirModule,
            lirModule,
            preOptimizationAsmModule,
            emitResult.AsmModule,
            emitResult.AssemblyText);
    }
}
