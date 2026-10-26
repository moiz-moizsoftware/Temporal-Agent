using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orchestrator.Activities.Infrastructure;
using Orchestrator.Activities.Telemetry;
using Orchestrator.Contracts;

namespace Orchestrator.Activities.Agent;

/// <summary>Runs Claude Code headless in the worktree. Output goes to the log dir; only metrics are returned.</summary>
public class ClaudeCodeRunner(IOptions<OrchestratorOptions> options, IOptions<LangfuseOptions> langfuse) : IAgentRunner
{
    public async Task<RunAgentResult> RunAsync(RunAgentInput input, CancellationToken ct)
    {
        var paths = new RalphPaths(input.WorktreePath, input.FeatureId);
        var outputPath = paths.LogFile(input.StoryId, input.Iteration, "agent.json");
        var stdErrPath = paths.LogFile(input.StoryId, input.Iteration, "agent.stderr.log");
        DeleteIfExists(outputPath, stdErrPath); // a retried attempt starts with clean logs

        using var span = OrchestratorTelemetry.Source.StartActivity("claude-code");
        var arguments = ClaudeCodeArguments.Build(options.Value, input.FeatureId);
        var stopwatch = Stopwatch.StartNew();
        var exitCode = await ProcessRunner.RunToFileAsync(
            options.Value.ClaudeCliPath,
            arguments,
            paths.Worktree,
            outputPath,
            stdErrPath,
            ct);
        stopwatch.Stop();

        var metrics = ClaudeResultParser.Parse(await File.ReadAllTextAsync(outputPath, ct));
        RecordGeneration(
            span, metrics, exitCode, prompt: arguments[1], options.Value.AgentModel, langfuse.Value.CaptureContent); // [1] is the -p value
        return new RunAgentResult(
            exitCode, metrics.Turns, metrics.CostUsd, metrics.InputTokens, metrics.OutputTokens, stopwatch.Elapsed, outputPath);
    }

    /// <summary>One Langfuse generation per CLI run, with the CLI's own cost so Langfuse does not re-price it.</summary>
    internal static void RecordGeneration(
        Activity? span, ClaudeRunMetrics metrics, int exitCode, string prompt, string configuredModel, bool captureContent)
    {
        if (span is null)
        {
            return;
        }

        span.SetTag("langfuse.observation.type", "generation");
        span.SetTag("gen_ai.request.model", metrics.Model ?? configuredModel);
        span.SetTag("gen_ai.usage.input_tokens", metrics.InputTokens);
        span.SetTag("gen_ai.usage.output_tokens", metrics.OutputTokens);
        if (metrics.CostUsd is { } cost)
        {
            span.SetTag("langfuse.observation.cost_details", JsonSerializer.Serialize(new { total = cost }));
        }

        span.SetMetadata("turns", metrics.Turns);
        span.SetMetadata("exitCode", exitCode);
        span.SetMetadata("isError", metrics.IsError);
        if (captureContent)
        {
            span.SetTag("langfuse.observation.input", prompt);
            span.SetTag("langfuse.observation.output", metrics.ResultText);
        }

        if (exitCode != 0 || metrics.IsError == true)
        {
            span.SetStatus(ActivityStatusCode.Error, $"exit code {exitCode}");
            span.SetTag("langfuse.observation.level", "ERROR");
        }
    }

    private static void DeleteIfExists(params string[] files)
    {
        foreach (var file in files.Where(File.Exists))
        {
            File.Delete(file);
        }
    }
}
