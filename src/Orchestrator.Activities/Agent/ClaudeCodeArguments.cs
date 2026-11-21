using System.Globalization;
using Orchestrator.Activities.Infrastructure;
using Orchestrator.Contracts;

namespace Orchestrator.Activities.Agent;

/// <summary>
/// The only place Claude Code CLI flags are built. Flags verified against `claude --help` (v2.1.0);
/// --max-turns is not listed in --help but was verified with a real headless call.
/// Never add a flag that skips permission checks.
/// </summary>
public static class ClaudeCodeArguments
{
    public static IReadOnlyList<string> Build(
        OrchestratorOptions options, string featureId, AgentProfile profile = AgentProfile.Implement)
    {
        var allowedTools = profile switch
        {
            AgentProfile.DraftStories => options.StoryAuthoringAllowedTools,
            AgentProfile.DraftTests => options.TestAuthoringAllowedTools,
            _ => options.AllowedTools,
        };

        var args = new List<string>
        {
            "-p", $"Follow the instructions in .ralph/{featureId}/{RalphPaths.PromptFileName(profile)}",
            "--output-format", "json",
            "--model", options.AgentModel,
            "--permission-mode", "acceptEdits",
            "--allowedTools", allowedTools,
            "--max-budget-usd", options.MaxBudgetUsdPerRun.ToString(CultureInfo.InvariantCulture),
            "--no-session-persistence",
        };

        if (options.MaxTurns is { } maxTurns)
        {
            args.AddRange(["--max-turns", maxTurns.ToString(CultureInfo.InvariantCulture)]);
        }

        return args;
    }
}
