using System.Diagnostics;
using Microsoft.Extensions.Options;
using Orchestrator.Activities.Infrastructure;
using Orchestrator.Contracts;

namespace Orchestrator.Activities.Agent;

/// <summary>Runs Claude Code headless in the worktree. Output goes to the log dir; only metrics are returned.</summary>
public class ClaudeCodeRunner(IOptions<OrchestratorOptions> options) : IAgentRunner
{
    public async Task<RunAgentResult> RunAsync(RunAgentInput input, CancellationToken ct)
    {
        var paths = new RalphPaths(input.WorktreePath, input.FeatureId);
        var outputPath = paths.LogFile(input.StoryId, input.Iteration, "agent.json");
        var stdErrPath = paths.LogFile(input.StoryId, input.Iteration, "agent.stderr.log");
        DeleteIfExists(outputPath, stdErrPath); // a retried attempt starts with clean logs

        var stopwatch = Stopwatch.StartNew();
        var exitCode = await ProcessRunner.RunToFileAsync(
            options.Value.ClaudeCliPath,
            ClaudeCodeArguments.Build(options.Value, input.FeatureId),
            paths.Worktree,
            outputPath,
            stdErrPath,
            ct);
        stopwatch.Stop();

        var metrics = ClaudeResultParser.Parse(await File.ReadAllTextAsync(outputPath, ct));
        return new RunAgentResult(
            exitCode, metrics.Turns, metrics.CostUsd, metrics.InputTokens, metrics.OutputTokens, stopwatch.Elapsed, outputPath);
    }

    private static void DeleteIfExists(params string[] files)
    {
        foreach (var file in files.Where(File.Exists))
        {
            File.Delete(file);
        }
    }
}
