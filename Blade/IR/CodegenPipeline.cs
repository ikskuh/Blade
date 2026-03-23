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
            // a physical self-move (`MOV _rN, _rN`). Run just that cleanup again
            // after allocation so these artifacts do not survive into final PASM.
            if (options.EnableAsmOptimization)
            {
                IReadOnlyList<string> postRegisterAllocationOptimizations = GetPostRegisterAllocationOptimizations(
                    options.EnabledAsmirOptimizations);
                if (postRegisterAllocationOptimizations.Count > 0)
                    asmModule = AsmOptimizer.Optimize(asmModule, postRegisterAllocationOptimizations);
            }
        }

        // Legalization: AUGS/AUGD for large immediates, size checks
        if (options.EnableLegalization)
            asmModule = AsmLegalizer.Legalize(asmModule);

        string assemblyText = FinalAssemblyWriter.Write(asmModule);
        return new EmitResult(asmModule, assemblyText);
    }

    private static IReadOnlyList<string> GetPostRegisterAllocationOptimizations(
        IReadOnlyList<string> enabledAsmirOptimizations)
    {
        Requires.NotNull(enabledAsmirOptimizations);

        List<string> optimizations = [];
        foreach (string optimization in enabledAsmirOptimizations)
        {
            if (optimization == "cleanup-self-mov")
                optimizations.Add(optimization);
        }

        return optimizations;
    }
}
