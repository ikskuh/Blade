using System;
using System.Collections.Generic;

namespace Blade.Reports;

/// <summary>
/// Describes one requested report output target.
/// </summary>
public sealed class ReportTarget(IReportWriter writer, string path, IReadOnlyDictionary<string, string> properties)
{
    /// <summary>
    /// Gets the report format to render.
    /// </summary>
    public IReportWriter Writer { get; } = writer;

    /// <summary>
    /// Gets the destination path, or <c>-</c> for stdout.
    /// </summary>
    public string Path { get; } = Requires.NotNullOrWhiteSpace(path);

    /// <summary>
    /// Gets the format-specific properties passed to the report writer.
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; } = CopyProperties(properties);

    private static IReadOnlyDictionary<string, string> CopyProperties(IReadOnlyDictionary<string, string> properties)
    {
        Requires.NotNull(properties);

        Dictionary<string, string> copy = new(properties.Count, StringComparer.Ordinal);
        foreach ((string key, string value) in properties)
        {
            Requires.NotNullOrWhiteSpace(key);
            Requires.NotNull(value);
            copy.Add(key, value);
        }

        return copy;
    }
}

/// <summary>
/// Enumerates the supported report formats.
/// </summary>
public enum ReportFormat
{
    /// <summary>
    /// Assembly-only compiler output
    /// </summary>
    Assembly,

    /// <summary>
    /// Plain-text report output.
    /// </summary>
    Text,

    /// <summary>
    /// HTML report output.
    /// </summary>
    Html,

    /// <summary>
    /// JSON report output.
    /// </summary>
    Json,
}
