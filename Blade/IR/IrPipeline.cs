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
        mirModule = MirInliner.InlineMandatoryAndSingleCallsite(
            mirModule,
            options.EnableSingleCallsiteInlining);

        if (options.EnableMirOptimizations)
        {
            mirModule = MirOptimizer.Optimize(
                mirModule,
                options.MaxOptimizationIterations,
                enableCostBasedInlining: options.EnableMirInlining);
        }

        LirModule lirModule = LirLowerer.Lower(mirModule);
        AsmModule asmModule = AsmLowerer.Lower(lirModule);

        IrBuildResult preEmit = new(boundProgram, mirModule, lirModule, asmModule, assemblyText: string.Empty);
        EmitResult emitResult = CodegenPipeline.Emit(preEmit, new EmitOptions());
        return new IrBuildResult(boundProgram, mirModule, lirModule, emitResult.AsmModule, emitResult.AssemblyText);
    }
}
