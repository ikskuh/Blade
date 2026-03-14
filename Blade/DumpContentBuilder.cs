using System.Collections.Generic;
using Blade.IR;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics.Bound;

namespace Blade;

public sealed class DumpSelection
{
    public bool DumpBound { get; init; }
    public bool DumpMirPreOptimization { get; init; }
    public bool DumpMir { get; init; }
    public bool DumpLirPreOptimization { get; init; }
    public bool DumpLir { get; init; }
    public bool DumpAsmirPreOptimization { get; init; }
    public bool DumpAsmir { get; init; }
    public bool DumpFinalAsm { get; init; }
}

public static class DumpContentBuilder
{
    public static Dictionary<string, string> Build(DumpSelection selection, IrBuildResult buildResult)
    {
        Requires.NotNull(selection);
        Requires.NotNull(buildResult);

        Dictionary<string, string> dumps = [];
        if (!selection.DumpBound
            && !selection.DumpMirPreOptimization
            && !selection.DumpMir
            && !selection.DumpLirPreOptimization
            && !selection.DumpLir
            && !selection.DumpAsmirPreOptimization
            && !selection.DumpAsmir
            && !selection.DumpFinalAsm)
        {
            dumps["40_final.spin2"] = buildResult.AssemblyText;
            return dumps;
        }

        if (selection.DumpBound)
            dumps["00_bound.ir"] = BoundTreeWriter.Write(buildResult.BoundProgram);
        if (selection.DumpMirPreOptimization)
            dumps["05_mir_preopt.ir"] = MirTextWriter.Write(buildResult.PreOptimizationMirModule);
        if (selection.DumpMir)
            dumps["10_mir.ir"] = MirTextWriter.Write(buildResult.MirModule);
        if (selection.DumpLirPreOptimization)
            dumps["15_lir_preopt.ir"] = LirTextWriter.Write(buildResult.PreOptimizationLirModule);
        if (selection.DumpLir)
            dumps["20_lir.ir"] = LirTextWriter.Write(buildResult.LirModule);
        if (selection.DumpAsmirPreOptimization)
            dumps["25_asmir_preopt.ir"] = AsmTextWriter.Write(buildResult.PreOptimizationAsmModule);
        if (selection.DumpAsmir)
            dumps["30_asmir.ir"] = AsmTextWriter.Write(buildResult.AsmModule);
        if (selection.DumpFinalAsm)
            dumps["40_final.spin2"] = buildResult.AssemblyText;
        return dumps;
    }
}
