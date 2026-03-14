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
            asmModule = RegisterAllocator.Allocate(asmModule);

        // Legalization: AUGS/AUGD for large immediates, size checks
        if (options.EnableLegalization)
            asmModule = AsmLegalizer.Legalize(asmModule);

        string assemblyText = FinalAssemblyWriter.Write(asmModule);
        return new EmitResult(asmModule, assemblyText);
    }
}
