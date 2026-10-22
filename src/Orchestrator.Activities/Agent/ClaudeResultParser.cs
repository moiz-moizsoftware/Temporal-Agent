using System.Text.Json;

namespace Orchestrator.Activities.Agent;

/// <summary>Metrics read from `claude -p --output-format json`. Every field is optional.</summary>
public sealed record ClaudeRunMetrics(int? Turns, decimal? CostUsd, long? InputTokens, long? OutputTokens, bool? IsError)
{
    public static readonly ClaudeRunMetrics Empty = new(null, null, null, null, null);
}

public static class ClaudeResultParser
{
    /// <summary>Never throws: unknown or broken output yields <see cref="ClaudeRunMetrics.Empty"/> fields.</summary>
    public static ClaudeRunMetrics Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ClaudeRunMetrics.Empty;
            }

            var usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : (JsonElement?)null;
            long? inputTokens = usage is { } us
                ? Sum(Long(us, "input_tokens"), Long(us, "cache_creation_input_tokens"), Long(us, "cache_read_input_tokens"))
                : null;

            return new ClaudeRunMetrics(
                Turns: (int?)Long(root, "num_turns"),
                CostUsd: root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number
                    ? cost.GetDecimal()
                    : null,
                InputTokens: inputTokens,
                OutputTokens: usage is { } uo ? Long(uo, "output_tokens") : null,
                IsError: root.TryGetProperty("is_error", out var err) && err.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? err.GetBoolean()
                    : null);
        }
        catch (JsonException)
        {
            return ClaudeRunMetrics.Empty;
        }
    }

    private static long? Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var l)
            ? l
            : null;

    private static long? Sum(params long?[] values) =>
        values.Any(v => v.HasValue) ? values.Sum(v => v ?? 0) : null;
}
