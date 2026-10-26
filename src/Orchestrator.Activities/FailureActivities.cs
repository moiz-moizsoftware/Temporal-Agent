using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Activities.Infrastructure;
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
        var paths = new RalphPaths(input.WorktreePath, input.FeatureId);
        var (body, usedFallback) = input.ReviewerFeedback is { } feedback
            ? (feedback, false)
            : await SummarizeLogAsync(input.RawLogPath, ct);

        var content = $"# Last failure: story {input.StoryId}, iteration {input.Iteration}\n\n{body}\n\nRaw log: `{input.RawLogPath}`\n";
        Directory.CreateDirectory(paths.Abs(paths.RalphDirRel));
        await File.WriteAllTextAsync(paths.Abs(paths.LastFailureRel), content, ct);
        await Git.RunAsync(paths.Worktree, ct, "add", "--", paths.LastFailureRel);

        return new SummarizeFailureResult(paths.Abs(paths.LastFailureRel), usedFallback);
    }

    private async Task<(string Body, bool UsedFallback)> SummarizeLogAsync(string rawLogPath, CancellationToken ct)
    {
        var tail = ReadTail(rawLogPath, options.Value.RawLogTailBytes);
        try
        {
            var response = await chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.System, Instructions), new ChatMessage(ChatRole.User, tail)],
                new ChatOptions { MaxOutputTokens = 1000 },
                ct);
            return string.IsNullOrWhiteSpace(response.Text)
                ? throw new InvalidOperationException("Empty summary.")
                : (response.Text, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ActivityExecutionContext.Current.Logger.LogWarning(ex, "Failure summarizer failed; writing raw log tail instead.");
            return ($"Summarizer unavailable. Last {options.Value.RawLogTailBytes} bytes of the raw log:\n\n```\n{tail}\n```", true);
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
