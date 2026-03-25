using System;
using System.Collections.Generic;
using Blade.IR;

namespace Blade;

internal sealed class CommandLineOptions
{
    internal CommandLineOptions()
    {
    }

    public required string FilePath { get; init; }
    public bool DumpBound { get; init; }
    public bool DumpMirPreOptimization { get; init; }
    public bool DumpMir { get; init; }
    public bool DumpLirPreOptimization { get; init; }
    public bool DumpLir { get; init; }
    public bool DumpAsmirPreOptimization { get; init; }
    public bool DumpAsmir { get; init; }
    public bool DumpFinalAsm { get; init; }
    public string? DumpDirectory { get; init; }
    public bool Json { get; init; }
    public string? OutputPath { get; init; }
    public bool EnableSingleCallsiteInlining { get; init; }
    public IReadOnlyList<MirOptimization> EnabledMirOptimizations { get; init; } = OptimizationRegistry.AllMirOptimizations;
    public IReadOnlyList<LirOptimization> EnabledLirOptimizations { get; init; } = OptimizationRegistry.AllLirOptimizations;
    public IReadOnlyList<AsmOptimization> EnabledAsmirOptimizations { get; init; } = OptimizationRegistry.AllAsmOptimizations;
    public IReadOnlyDictionary<string, string> NamedModuleRoots { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public int ComptimeFuel { get; init; }
}
