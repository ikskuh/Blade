using Blade.IR.Asm;

namespace Blade.IR;

public static class CodegenPipeline
{
    public static EmitResult Emit(IrBuildResult buildResult, EmitOptions? options = null)
    {
        options ??= new EmitOptions();
        AsmModule asmModule = options.EnableAsmOptimization
            ? AsmOptimizer.Optimize(buildResult.AsmModule)
            : buildResult.AsmModule;

        string assemblyText = FinalAssemblyWriter.Write(asmModule);
        return new EmitResult(asmModule, assemblyText);
    }
}
