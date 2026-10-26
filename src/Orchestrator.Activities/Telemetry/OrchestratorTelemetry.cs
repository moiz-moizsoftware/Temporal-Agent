using System.Diagnostics;

namespace Orchestrator.Activities.Telemetry;

/// <summary>
/// Span helpers. Attribute names follow Langfuse's OpenTelemetry mapping (langfuse.* and gen_ai.*). With no
/// listener attached (tracing disabled, or tests) every helper is a no-op.
/// </summary>
public static class OrchestratorTelemetry
{
    public const string SourceName = "Orchestrator";

    /// <summary>Source used by Microsoft.Extensions.AI's OpenTelemetry chat client for the failure summarizer.</summary>
    public const string SummarizerSourceName = "Orchestrator.Summarizer";

    public static readonly ActivitySource Source = new(SourceName);

    /// <summary>Tags the current span (Temporal's RunActivity span) with the story it works on.</summary>
    public static void TagStory(string featureId, string storyId, int iteration)
    {
        var span = Activity.Current;
        span.SetMetadata("featureId", featureId);
        span.SetMetadata("storyId", storyId);
        span.SetMetadata("iteration", iteration);
        span?.SetTag("langfuse.trace.metadata.featureId", featureId);
        span?.SetTag("langfuse.trace.tags", new[] { featureId });
    }

    /// <summary>Adds observation metadata to the current span.</summary>
    public static void SetCurrentMetadata(string key, object? value) => Activity.Current.SetMetadata(key, value);

    public static void SetMetadata(this Activity? span, string key, object? value) =>
        span?.SetTag($"langfuse.observation.metadata.{key}", value);
}
