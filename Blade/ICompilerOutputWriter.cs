namespace Blade;

internal interface ICompilerOutputWriter
{
    /// <summary>
    /// Writes the requested compiler output.
    /// </summary>
    bool TryWrite(
        CommandLineOptions options,
        CompilationOutput compilation,
        out int exitCode,
        out string? error);
}
