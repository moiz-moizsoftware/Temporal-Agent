using Orchestrator.Contracts;

namespace Orchestrator.Activities.Agent;

/// <summary>Runs a coding agent once, with fresh context, inside the worktree.</summary>
public interface IAgentRunner
{
    Task<RunAgentResult> RunAsync(RunAgentInput input, CancellationToken ct);
}
