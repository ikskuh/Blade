using System;
using System.Collections.Generic;
using Blade.IR;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics.Bound;

namespace Blade.Tests;

internal readonly struct TestIrBuildResult
{
    private readonly Blade.IR.IrBuildResult _inner;

    public TestIrBuildResult(Blade.IR.IrBuildResult inner)
    {
        _inner = Requires.NotNull(inner);
    }

    public TestIrBuildResult(
        BoundProgram boundProgram,
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
        : this(new Blade.IR.IrBuildResult(
            boundProgram,
            imagePlan,
            imagePlacement,
            layoutSolution,
            cogResourceLayouts,
            [preOptimizationMirModule],
            [mirModule],
            [preOptimizationLirModule],
            [lirModule],
            [preOptimizationAsmModule],
            [asmModule],
            assemblyText))
    {
    }

    public BoundProgram BoundProgram => _inner.BoundProgram;

    public ImagePlan ImagePlan => _inner.ImagePlan;

    public ImagePlacement ImagePlacement => _inner.ImagePlacement;

    public LayoutSolution LayoutSolution => _inner.LayoutSolution;

    public CogResourceLayoutSet CogResourceLayouts => _inner.CogResourceLayouts;

    public IReadOnlyList<MirModule> PreOptimizationMirModules => _inner.PreOptimizationMirModules;

    public MirModule PreOptimizationMirModule => GetSingleModule(_inner.PreOptimizationMirModules, "pre-optimization MIR");

    public IReadOnlyList<MirModule> MirModules => _inner.MirModules;

    public MirModule MirModule => GetSingleModule(_inner.MirModules, "MIR");

    public IReadOnlyList<LirModule> PreOptimizationLirModules => _inner.PreOptimizationLirModules;

    public LirModule PreOptimizationLirModule => GetSingleModule(_inner.PreOptimizationLirModules, "pre-optimization LIR");

    public IReadOnlyList<LirModule> LirModules => _inner.LirModules;

    public LirModule LirModule => GetSingleModule(_inner.LirModules, "LIR");

    public IReadOnlyList<AsmModule> PreOptimizationAsmModules => _inner.PreOptimizationAsmModules;

    public AsmModule PreOptimizationAsmModule => GetSingleModule(_inner.PreOptimizationAsmModules, "pre-optimization ASMIR");

    public IReadOnlyList<AsmModule> AsmModules => _inner.AsmModules;

    public AsmModule AsmModule => GetSingleModule(_inner.AsmModules, "ASMIR");

    public string AssemblyText => _inner.AssemblyText;

    public static implicit operator TestIrBuildResult(Blade.IR.IrBuildResult build)
    {
        return new TestIrBuildResult(build);
    }

    public static implicit operator Blade.IR.IrBuildResult(TestIrBuildResult build)
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