using System.Collections.Generic;
using Blade.Diagnostics;
using Blade.IR.Asm;

namespace Blade.IR;

public static class CodegenPipeline
{
    public static EmitResult Emit(IrBuildResult buildResult, EmitOptions? options = null, DiagnosticBag? diagnostics = null)
    {
        Requires.NotNull(buildResult);

        options ??= new EmitOptions();
        AsmModule asmModule = buildResult.AsmModule;

        // Peephole optimization
        if (options.EnableAsmOptimization)
            asmModule = AsmOptimizer.Optimize(asmModule, options.EnabledAsmirOptimizations);

        // Legalization before COG resource layout fixes the code size seen by the planner.
        if (options.EnableLegalization)
            asmModule = AsmLegalizer.Legalize(asmModule);

        CogResourceLayoutSet cogResourceLayouts = CogResourcePlanner.Build(
            asmModule,
            buildResult.ImagePlan,
            buildResult.ImagePlacement,
            buildResult.LayoutSolution,
            includeDefaultBladeHalt: true,
            diagnostics);
        if (diagnostics?.HasErrors == true)
            return new EmitResult(asmModule, cogResourceLayouts, string.Empty);

        // Register allocation: virtual → physical
        if (options.EnableRegisterAllocation)
        {
            asmModule = RegisterAllocator.AllocateWithinImage(asmModule, cogResourceLayouts);

            // Register coalescing can turn a useful virtual-register move into
            // a physical self-move (`MOV _rN, _rN`). Run post-regalloc-eligible
            // optimizations again so these artifacts do not survive into final PASM.
            if (options.EnableAsmOptimization)
            {
                List<AsmOptimization> postRegAllocOptimizations = [];
                foreach (AsmOptimization optimization in options.EnabledAsmirOptimizations)
                {
                    if ((optimization.State & AsmOptimizationState.PostRegAlloc) != 0)
                        postRegAllocOptimizations.Add(optimization);
                }

                if (postRegAllocOptimizations.Count > 0)
                    asmModule = AsmOptimizer.Optimize(asmModule, postRegAllocOptimizations);
            }
        }

        FinalAssembly finalAssembly = FinalAssemblyWriter.Build(asmModule, cogResourceLayouts);
        return new EmitResult(asmModule, cogResourceLayouts, finalAssembly.Text);
    }
}
