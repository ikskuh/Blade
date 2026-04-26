using System.Text.Json.Serialization;

namespace Blade;

/// <summary>
/// Represents one rendered compiler dump artifact.
/// A dump artifact is the single source-of-truth unit shared by JSON reports,
/// text/stdout rendering, and dump-directory emission.
/// </summary>
public sealed class DumpArtifact(
    string id,
    string title,
    string fileName,
    string content)
{
    /// <summary>
    /// Gets the stable machine-readable dump identifier used by tests and tooling.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; } = Requires.NotNullOrWhiteSpace(id);

    /// <summary>
    /// Gets the human-readable dump title shown in reports and previews.
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; } = Requires.NotNullOrWhiteSpace(title);

    /// <summary>
    /// Gets the canonical output filename for the dump artifact.
    /// </summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; } = Requires.NotNullOrWhiteSpace(fileName);

    /// <summary>
    /// Gets the rendered textual dump content.
    /// </summary>
    [JsonPropertyName("content")]
    public string Content { get; } = Requires.NotNull(content);
}
