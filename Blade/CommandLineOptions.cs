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
    public IReadOnlyList<ReportTarget> ReportTargets { get; init; } = [];
    public bool EnableSingleCallsiteInlining { get; init; }
    public IReadOnlyList<MirOptimization> EnabledMirOptimizations { get; init; } = OptimizationRegistry.AllMirOptimizations;
    public IReadOnlyList<LirOptimization> EnabledLirOptimizations { get; init; } = OptimizationRegistry.AllLirOptimizations;
    public IReadOnlyList<AsmOptimization> EnabledAsmirOptimizations { get; init; } = OptimizationRegistry.AllAsmOptimizations;
    public IReadOnlyDictionary<string, string> NamedModuleRoots { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public int ComptimeFuel { get; init; }
    public string? RuntimeLauncherPath { get; init; }
}
