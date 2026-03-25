using System.Linq;
using Blade.Diagnostics;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.IR.Mir.Optimizations;
using Blade.Semantics.Bound;

namespace Blade.IR;

public static class IrPipeline
{
    public static IrBuildResult Build(BoundProgram boundProgram, IrPipelineOptions? options = null, DiagnosticBag? diagnostics = null)
    {
        options ??= new IrPipelineOptions();

        MirModule mirModule = MirLowerer.Lower(boundProgram);

        bool enableSingleCallsiteInlining = options.EnableSingleCallsiteInlining
            && options.EnabledMirOptimizations.Any(static o => o is MirSingleCallsiteInline);

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
