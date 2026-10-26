using System.Diagnostics;
using OpenTelemetry;
using Orchestrator.Activities.Telemetry;
using Temporalio.Activities;

namespace Orchestrator.Worker;

/// <summary>
/// Groups every span of a workflow run under one Langfuse session (the workflow id), so retries and
/// continued runs of the same feature show up together.
/// </summary>
internal sealed class LangfuseEnrichmentProcessor(string environment, string? workflowIdTag) : BaseProcessor<Activity>
{
    public override void OnStart(Activity span)
    {
        span.SetTag("langfuse.environment", environment);
        if (!ActivityExecutionContext.HasCurrent)
        {
            return;
        }

        var info = ActivityExecutionContext.Current.Info;
        span.SetTag("langfuse.session.id", info.WorkflowId);
        span.SetTag("langfuse.trace.name", info.WorkflowType);
        span.SetMetadata("activityType", info.ActivityType);
        span.SetMetadata("attempt", info.Attempt);
    }

    public override void OnEnd(Activity span)
    {
        // Workflow spans have no activity context; the Temporal interceptor tags them with the workflow id instead.
        if (workflowIdTag is not null
            && span.GetTagItem("langfuse.session.id") is null
            && span.GetTagItem(workflowIdTag) is string workflowId)
        {
            span.SetTag("langfuse.session.id", workflowId);
        }
    }
}
