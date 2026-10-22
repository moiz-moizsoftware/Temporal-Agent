using System.Globalization;

namespace Orchestrator.Activities.Agent;

/// <summary>
/// The only place Claude Code CLI flags are built. Flags verified against `claude --help` (v2.1.0);
/// --max-turns is not listed in --help but was verified with a real headless call.
/// Never add a flag that skips permission checks.
/// </summary>
public static class ClaudeCodeArguments
{
    public static IReadOnlyList<string> Build(OrchestratorOptions options, string featureId)
    {
        var args = new List<string>
        {
            "-p", $"Follow the instructions in .ralph/{featureId}/PROMPT.md",
            "--output-format", "json",
            "--model", options.AgentModel,
            "--permission-mode", "acceptEdits",
            "--allowedTools", options.AllowedTools,
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
