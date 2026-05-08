using System.Text.Json.Serialization;

namespace Blade;

/// <summary>
/// Captures summary metrics for one compilation attempt.
/// </summary>
public sealed class CompilationMetrics
{
    /// <summary>
    /// Gets an empty metrics object used before final timing is known.
    /// </summary>
    public static CompilationMetrics Empty { get; } = new()
    {
        TokenCount = 0,
        MemberCount = 0,
        BoundFunctionCount = 0,
        MirFunctionCount = 0,
        TimeMs = 0,
    };

    /// <summary>
    /// Gets the number of tokens in the root module.
    /// </summary>
    [JsonPropertyName("token_count")]
    public required int TokenCount { get; init; }

    /// <summary>
    /// Gets the number of top-level syntax members in the root module.
    /// </summary>
    [JsonPropertyName("member_count")]
    public required int MemberCount { get; init; }

    /// <summary>
    /// Gets the number of bound functions.
    /// </summary>
    [JsonPropertyName("bound_function_count")]
    public required int BoundFunctionCount { get; init; }

    /// <summary>
    /// Gets the number of optimized MIR functions.
    /// </summary>
    [JsonPropertyName("mir_function_count")]
    public required int MirFunctionCount { get; init; }

    /// <summary>
    /// Gets the total wall-clock compilation time in milliseconds.
    /// </summary>
    [JsonPropertyName("time_ms")]
    public required double TimeMs { get; init; }
}
