using System;
using System.Collections.Generic;
using Blade.IR;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;

namespace Blade.Tests;

internal readonly struct TestCompilationStages
{
    private readonly CompilationStageOutput _inner;

    public TestCompilationStages(CompilationStageOutput inner)
    {
        _inner = Requires.NotNull(inner);
    }

    public TestCompilationStages(
        ImagePlan imagePlan,
        ImagePlacement imagePlacement,
        LayoutSolution layoutSolution,
        CogResourceLayoutSet cogResourceLayouts,
        MirModule preOptimizationMirModule,
        MirModule mirModule,
        LirModule preOptimizationLirModule,
        LirModule lirModule,
        AsmModule preOptimizationAsmModule,
        AsmModule asmModule,
        string assemblyText)
        : this(new CompilationStageOutput
        {
            ImagePlan = imagePlan,
            ImagePlacement = imagePlacement,
            LayoutSolution = layoutSolution,
            CogResourceLayouts = cogResourceLayouts,
            PreOptimizationMirModules = [preOptimizationMirModule],
            MirModules = [mirModule],
            PreOptimizationLirModules = [preOptimizationLirModule],
            LirModules = [lirModule],
            PreOptimizationAsmModules = [preOptimizationAsmModule],
            AsmModules = [asmModule],
            AssemblyText = assemblyText,
        })
    {
    }

    public ImagePlan ImagePlan => Requires.NotNull(_inner.ImagePlan);

    public ImagePlacement ImagePlacement => Requires.NotNull(_inner.ImagePlacement);

    public LayoutSolution LayoutSolution => Requires.NotNull(_inner.LayoutSolution);

    public CogResourceLayoutSet CogResourceLayouts => Requires.NotNull(_inner.CogResourceLayouts);

    public IReadOnlyList<MirModule> PreOptimizationMirModules => Requires.NotNull(_inner.PreOptimizationMirModules);

    public MirModule PreOptimizationMirModule => GetSingleModule(PreOptimizationMirModules, "pre-optimization MIR");

    public IReadOnlyList<MirModule> MirModules => Requires.NotNull(_inner.MirModules);

    public MirModule MirModule => GetSingleModule(MirModules, "MIR");

    public IReadOnlyList<LirModule> PreOptimizationLirModules => Requires.NotNull(_inner.PreOptimizationLirModules);

    public LirModule PreOptimizationLirModule => GetSingleModule(PreOptimizationLirModules, "pre-optimization LIR");

    public IReadOnlyList<LirModule> LirModules => Requires.NotNull(_inner.LirModules);

    public LirModule LirModule => GetSingleModule(LirModules, "LIR");

    public IReadOnlyList<AsmModule> PreOptimizationAsmModules => Requires.NotNull(_inner.PreOptimizationAsmModules);

    public AsmModule PreOptimizationAsmModule => GetSingleModule(PreOptimizationAsmModules, "pre-optimization ASMIR");

    public IReadOnlyList<AsmModule> AsmModules => Requires.NotNull(_inner.AsmModules);

    public AsmModule AsmModule => GetSingleModule(AsmModules, "ASMIR");

    public string AssemblyText => Requires.NotNull(_inner.AssemblyText);

    public static implicit operator TestCompilationStages(CompilationStageOutput build)
    {
        return new TestCompilationStages(build);
    }

    public static implicit operator CompilationStageOutput(TestCompilationStages build)
    {
        return build._inner;
    }

    private static TModule GetSingleModule<TModule>(IReadOnlyList<TModule> modules, string stage)
    {
        if (modules.Count != 1)
            throw new InvalidOperationException($"Expected exactly one {stage} module, but found {modules.Count}.");

        return modules[0];
    }
}
