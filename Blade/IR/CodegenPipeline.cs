using System.Collections.Generic;
using System.Linq;
using Blade.Diagnostics;
using Blade.IR.Asm;

namespace Blade.IR;

public static class CodegenPipeline
{
    /// <summary>
    /// Completes code generation for the supplied backend stage output.
    /// </summary>
    public static void Emit(CompilationStageOutput buildResult, EmitOptions? options = null, DiagnosticBag? diagnostics = null)
    {
        Requires.NotNull(buildResult);
        Assert.Invariant(buildResult.AsmModules is not null, "ASMIR modules must be populated before code generation.");
        Assert.Invariant(buildResult.ImagePlan is not null, "Image plan must be populated before code generation.");
        Assert.Invariant(buildResult.ImagePlacement is not null, "Image placement must be populated before code generation.");
        Assert.Invariant(buildResult.LayoutSolution is not null, "Layout solution must be populated before code generation.");

        options ??= new EmitOptions();
        List<AsmModule> asmModules = buildResult.AsmModules.ToList();

        // Peephole optimization
        if (options.EnableAsmOptimization)
        {
            for (int i = 0; i < asmModules.Count; i++)
                asmModules[i] = AsmOptimizer.Optimize(asmModules[i], options.EnabledAsmirOptimizations);
        }

        // Legalization before COG resource layout fixes the code size seen by the planner.
        if (options.EnableLegalization)
        {
            for (int i = 0; i < asmModules.Count; i++)
                asmModules[i] = AsmLegalizer.Legalize(asmModules[i]);
        }

        CogResourceLayoutSet cogResourceLayouts = CogResourcePlanner.Build(
            asmModules,
            buildResult.ImagePlan,
            buildResult.ImagePlacement,
            buildResult.LayoutSolution,
            includeDefaultBladeHalt: true,
            diagnostics);
        if (diagnostics?.HasErrors == true)
        {
            buildResult.AsmModules = asmModules;
            buildResult.CogResourceLayouts = cogResourceLayouts;
            buildResult.AssemblyText = null;
            return;
        }

        // Register allocation: virtual → physical
        if (options.EnableRegisterAllocation)
        {
            for (int i = 0; i < asmModules.Count; i++)
                asmModules[i] = RegisterAllocator.AllocateWithinImage(asmModules[i], cogResourceLayouts);

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
                {
                    for (int i = 0; i < asmModules.Count; i++)
                        asmModules[i] = AsmOptimizer.Optimize(asmModules[i], postRegAllocOptimizations);
                }
            }
        }

        buildResult.AsmModules = asmModules;
        buildResult.CogResourceLayouts = cogResourceLayouts;
        buildResult.AssemblyText = FinalAssemblyWriter.Write(asmModules, cogResourceLayouts);
    }
}
