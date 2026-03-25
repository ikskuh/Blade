using System.Collections.Generic;
using Blade.IR.Asm;

namespace Blade.IR;

public static class CodegenPipeline
{
    public static EmitResult Emit(IrBuildResult buildResult, EmitOptions? options = null)
    {
        Requires.NotNull(buildResult);

        options ??= new EmitOptions();
        AsmModule asmModule = buildResult.AsmModule;

        // Peephole optimization
        if (options.EnableAsmOptimization)
            asmModule = AsmOptimizer.Optimize(asmModule, options.EnabledAsmirOptimizations);

        // Register allocation: virtual → physical
        if (options.EnableRegisterAllocation)
        {
            asmModule = RegisterAllocator.Allocate(asmModule);

            // Register coalescing can turn a useful virtual-register move into
            // a physical self-move (`MOV _rN, _rN`). Run post-regalloc-eligible
            // optimizations again so these artifacts do not survive into final PASM.
            if (options.EnableAsmOptimization)
            {
                IReadOnlyList<IAsmOptimization> postRegAllocOptimizations =
                    OptimizationRegistry.GetAsmOptimizationsForState(
                        AsmOptimizationState.PostRegAlloc,
                        options.EnabledAsmirOptimizations);
                if (postRegAllocOptimizations.Count > 0)
                    asmModule = AsmOptimizer.Optimize(asmModule, postRegAllocOptimizations);
            }
        }

        // Legalization: AUGS/AUGD for large immediates, size checks
        if (options.EnableLegalization)
            asmModule = AsmLegalizer.Legalize(asmModule);

        string assemblyText = FinalAssemblyWriter.Write(asmModule);
        return new EmitResult(asmModule, assemblyText);
    }
}
