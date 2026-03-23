using Blade.IR;

namespace Blade;

internal interface ICompilerOutputWriter
{
    bool TryWrite(
        CommandLineOptions options,
        CompilationResult compilation,
        CompilationMetrics metrics,
        out int exitCode,
        out string? error);
}
