using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Orchestrator.Activities.Telemetry;

public enum ScoreDataType
{
    Numeric,
    Boolean,
    Categorical,
}

/// <summary>
/// Posts Langfuse scores against the current trace. Best effort: failures are logged, never thrown, because
/// telemetry must not fail an activity.
/// </summary>
public interface ILangfuseScores
{
    /// <param name="key">Makes the score id stable, so an activity retry overwrites instead of duplicating.</param>
    /// <param name="onSpan">Attach to the current span rather than to the trace as a whole.</param>
    Task PostAsync(string name, object value, ScoreDataType dataType, string key, bool onSpan, CancellationToken ct);
}

public sealed class NullLangfuseScores : ILangfuseScores
{
    public static readonly NullLangfuseScores Instance = new();

    public Task PostAsync(string name, object value, ScoreDataType dataType, string key, bool onSpan, CancellationToken ct) =>
        Task.CompletedTask;
}

public sealed class LangfuseScoresClient(HttpClient http, IOptions<LangfuseOptions> options, ILogger<LangfuseScoresClient> logger)
    : ILangfuseScores
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task PostAsync(string name, object value, ScoreDataType dataType, string key, bool onSpan, CancellationToken ct)
    {
        // The OTel trace id is the Langfuse trace id. No span means the trace was not sampled or exported.
        if (Activity.Current is not { } span)
        {
            return;
        }

        var traceId = span.TraceId.ToHexString();
        var body = new
        {
            id = $"{traceId}-{name}-{key}",
            traceId,
            observationId = onSpan ? span.SpanId.ToHexString() : null,
            name,
            value,
            dataType = dataType.ToString().ToUpperInvariant(),
            environment = options.Value.Environment,
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.Value.BaseUrl.TrimEnd('/')}/api/public/scores")
            {
                Content = JsonContent.Create(body, options: Json),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", options.Value.BasicAuth);
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Posting Langfuse score {Score} failed.", name);
        }
    }
}
