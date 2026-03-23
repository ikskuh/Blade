using System.Text.Json.Serialization;

namespace Blade;

internal sealed class CompilationMetrics
{
    [JsonPropertyName("token_count")]
    public required int TokenCount { get; init; }

    [JsonPropertyName("member_count")]
    public required int MemberCount { get; init; }

    [JsonPropertyName("bound_function_count")]
    public required int BoundFunctionCount { get; init; }

    [JsonPropertyName("mir_function_count")]
    public required int MirFunctionCount { get; init; }

    [JsonPropertyName("time_ms")]
    public required double TimeMs { get; init; }
}
