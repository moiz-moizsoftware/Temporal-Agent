using Orchestrator.Activities.Agent;
using Orchestrator.Activities.Telemetry;
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
    public Task<RunAgentResult> RunAgentAsync(RunAgentInput input)
    {
        OrchestratorTelemetry.TagStory(input.FeatureId, input.StoryId, input.Iteration);
        return runner.RunAsync(input, ActivityExecutionContext.Current.CancellationToken);
    }
}
