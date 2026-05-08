namespace Blade;

/// <summary>
/// Describes one requested report output target.
/// </summary>
public sealed class ReportTarget(ReportFormat format, string path)
{
    /// <summary>
    /// Gets the report format to render.
    /// </summary>
    public ReportFormat Format { get; } = format;

    /// <summary>
    /// Gets the destination path, or <c>-</c> for stdout.
    /// </summary>
    public string Path { get; } = Requires.NotNullOrWhiteSpace(path);
}

/// <summary>
/// Enumerates the supported report formats.
/// </summary>
public enum ReportFormat
{
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
