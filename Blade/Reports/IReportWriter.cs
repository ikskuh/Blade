using System.IO;

namespace Blade.Reports;

/// <summary>
/// A report writer is a component that writes a human-readable report for a compilation result.
/// </summary>
public interface IReportWriter
{
    /// <summary>
    /// Writes the supplied compilation output in this report format.
    /// </summary>
    void Write(TextWriter writer, CompilationOutput report);
}
