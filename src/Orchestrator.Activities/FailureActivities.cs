using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Activities.Infrastructure;
using Orchestrator.Activities.Telemetry;
using Orchestrator.Contracts;
using Temporalio.Activities;

namespace Orchestrator.Activities;

public class FailureActivities(IChatClient chatClient, IOptions<OrchestratorOptions> options)
{
    private const string Instructions =
        """
        You summarize a failed build/test run for a coding agent that will retry the task.
        Write markdown, under 300 words, with three short sections:
        ## What failed  (exact failing tests, errors, or locked-file violations)
        ## Likely cause
        ## What to try next
        Quote error messages and file:line locations exactly. Do not invent details not in the log.
        """;

    /// <summary>
    /// Writes .ralph/{featureId}/last_failure.md from an LLM summary of the raw log tail. If the summarizer
    /// fails for any reason, the raw tail is written instead (UsedFallback = true). Reviewer feedback, when given,
    /// is written as-is: the build passed, so the log has nothing to summarize. Safe to retry: overwrites.
    /// </summary>
    [Activity(ActivityNames.SummarizeFailure)]
    public async Task<SummarizeFailureResult> SummarizeFailureAsync(SummarizeFailureInput input)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        OrchestratorTelemetry.TagStory(input.FeatureId, input.StoryId, input.Iteration);
        var paths = new RalphPaths(input.WorktreePath, input.FeatureId);
        var (body, usedFallback) = input.ReviewerFeedback is { } feedback
            ? (feedback, false)
            : await SummarizeLogAsync(input.RawLogPath, ct);

        var content = $"# Last failure: story {input.StoryId}, iteration {input.Iteration}\n\n{body}\n\nRaw log: `{input.RawLogPath}`\n";
        Directory.CreateDirectory(paths.Abs(paths.RalphDirRel));
        await File.WriteAllTextAsync(paths.Abs(paths.LastFailureRel), content, ct);
        await Git.RunAsync(paths.Worktree, ct, "add", "--", paths.LastFailureRel);
        OrchestratorTelemetry.SetCurrentMetadata("usedFallback", usedFallback);
        OrchestratorTelemetry.SetCurrentMetadata("hasReviewerFeedback", input.ReviewerFeedback is not null);

        return new SummarizeFailureResult(paths.Abs(paths.LastFailureRel), usedFallback);
    }

    /// <summary>
    /// Explains an unexpected failure that is about to stop the workflow. Never fails: when the summarizer is
    /// unavailable (no credits, no key, network) it returns the add-credits message plus the full stack trace.
    /// </summary>
    [Activity(ActivityNames.ExplainFailure)]
    public async Task<ExplainFailureResult> ExplainFailureAsync(ExplainFailureInput input)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        OrchestratorTelemetry.SetCurrentMetadata("failedStep", input.FailedStep);
        var (summary, error) = await TrySummarizeAsync(ExplainInstructions, input.Details, maxOutputTokens: 600, ct);
        OrchestratorTelemetry.SetCurrentMetadata("usedFallback", summary is null);
        return summary is null
            ? new ExplainFailureResult(FailureMessages.WithoutSummary(error, "full stack trace", input.Details), UsedFallback: true)
            : new ExplainFailureResult($"{summary}\n\nFull stack trace:\n\n{input.Details}", UsedFallback: false);
    }

    private const string ExplainInstructions =
        """
        A workflow step failed unexpectedly and stopped the run. Explain it to the developer who operates the workflow.
        Plain text, under 150 words: what failed, the likely cause, and what to do before resuming the workflow.
        Quote error messages and file:line locations exactly. Do not invent details not in the input.
        """;

    private async Task<(string Body, bool UsedFallback)> SummarizeLogAsync(string rawLogPath, CancellationToken ct)
    {
        var tail = ReadTail(rawLogPath, options.Value.RawLogTailBytes);
        var (summary, error) = await TrySummarizeAsync(Instructions, tail, maxOutputTokens: 1000, ct);
        return summary is null
            ? (FailureMessages.WithoutSummary(
                error, $"raw log (last {options.Value.RawLogTailBytes} bytes)", $"```\n{tail}\n```"), true)
            : (summary, false);
    }

    /// <summary>The summary, or null and the reason. Any summarizer error (including no credits) is swallowed.</summary>
    private async Task<(string? Summary, string? Error)> TrySummarizeAsync(
        string instructions, string content, int maxOutputTokens, CancellationToken ct)
    {
        try
        {
            var response = await chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.System, instructions), new ChatMessage(ChatRole.User, content)],
                new ChatOptions { MaxOutputTokens = maxOutputTokens },
                ct);
            return string.IsNullOrWhiteSpace(response.Text) ? (null, "the summary was empty") : (response.Text, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ActivityExecutionContext.Current.Logger.LogWarning(ex, "Failure summarizer failed; using the raw detail instead.");
            return (null, ex.Message.Split('\n')[0].Trim());
        }
    }

    internal static string ReadTail(string path, int maxBytes)
    {
        if (!File.Exists(path))
        {
            return $"(raw log not found: {path})";
        }

        using var stream = File.OpenRead(path);
        var start = Math.Max(0, stream.Length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
