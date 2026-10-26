using Orchestrator.Activities.Telemetry;
using Orchestrator.Contracts;
using Temporalio.Activities;

namespace Orchestrator.Activities;

public class TelemetryActivities(ILangfuseScores scores)
{
    /// <summary>
    /// Records the feature outcome as Langfuse scores on the workflow's trace. Best effort: score failures are
    /// logged by the client, and score ids are stable, so a retry overwrites.
    /// </summary>
    [Activity(ActivityNames.RecordFeatureOutcome)]
    public async Task RecordFeatureOutcomeAsync(RecordFeatureOutcomeInput input)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var key = input.FeatureId;
        await scores.PostAsync("feature_outcome", input.Outcome.ToString(), ScoreDataType.Categorical, key, onSpan: false, ct);
        await scores.PostAsync("iterations", input.Iterations, ScoreDataType.Numeric, key, onSpan: false, ct);
        await scores.PostAsync(
            "stories_passed_ratio",
            input.StoriesTotal == 0 ? 0 : (double)input.StoriesPassed / input.StoriesTotal,
            ScoreDataType.Numeric, key, onSpan: false, ct);
        if (input.Reason is { } reason)
        {
            await scores.PostAsync("escalation_reason", reason.ToString(), ScoreDataType.Categorical, key, onSpan: false, ct);
        }
    }
}
