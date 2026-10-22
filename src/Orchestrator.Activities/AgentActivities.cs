using Orchestrator.Activities.Agent;
using Orchestrator.Contracts;
using Temporalio.Activities;

namespace Orchestrator.Activities;

public class AgentActivities(IAgentRunner runner)
{
    /// <summary>
    /// Runs the agent once. A non-zero exit code is reported, not thrown: Verify decides whether the story passed.
    /// Heartbeats while the CLI runs (see ProcessRunner).
    /// </summary>
    [Activity(ActivityNames.RunAgent)]
    public Task<RunAgentResult> RunAgentAsync(RunAgentInput input) =>
        runner.RunAsync(input, ActivityExecutionContext.Current.CancellationToken);
}
