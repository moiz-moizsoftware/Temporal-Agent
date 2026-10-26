namespace Orchestrator.Activities;

/// <summary>
/// Langfuse tracing, bound from the "Langfuse" section and LANGFUSE__* env vars. The secret key is read only from
/// the LANGFUSE_SECRET_KEY env var. Tracing is off unless both keys are set.
/// </summary>
public sealed class LangfuseOptions
{
    public const string SectionName = "Langfuse";

    public const string SecretKeyVariable = "LANGFUSE_SECRET_KEY";

    /// <summary>Langfuse Cloud region or a self-hosted instance, without a trailing slash.</summary>
    public string BaseUrl { get; set; } = "https://cloud.langfuse.com";

    public string PublicKey { get; set; } = "";

    /// <summary>Not bound from config; set from <see cref="SecretKeyVariable"/> at startup.</summary>
    public string SecretKey { get; set; } = "";

    /// <summary>Send the summarizer prompt/response and the agent's final result text, not just metrics.</summary>
    public bool CaptureContent { get; set; } = true;

    public string Environment { get; set; } = "development";

    public bool Enabled => !string.IsNullOrWhiteSpace(PublicKey) && !string.IsNullOrWhiteSpace(SecretKey);

    public string BasicAuth => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{PublicKey}:{SecretKey}"));
}
