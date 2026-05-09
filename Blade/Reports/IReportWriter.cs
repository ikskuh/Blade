using System.Collections.Generic;
using System.IO;

namespace Blade.Reports;

/// <summary>
/// A report writer is a component that writes a human-readable report for a compilation result.
/// </summary>
public interface IReportWriter
{
    /// <summary>
    /// Gets the supported property keys for this report writer.
    /// </summary>
    IReadOnlySet<string> PropertyKeys { get; }

    /// <summary>
    /// Writes the supplied compilation output in this report format.
    /// </summary>
    void Write(TextWriter writer, CompilationOutput report, IReadOnlyDictionary<string, string> properties);
}
