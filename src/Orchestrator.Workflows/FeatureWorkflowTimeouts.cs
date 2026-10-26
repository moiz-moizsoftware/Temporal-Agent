using Temporalio.Common;
using Temporalio.Workflows;

namespace Orchestrator.Workflows;

internal static class FeatureWorkflowTimeouts
{
    /// <summary>Quick file/git activities.</summary>
    public static ActivityOptions Short => new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(2),
        RetryPolicy = new RetryPolicy { MaximumAttempts = 3 },
    };

    /// <summary>Best-effort telemetry. One attempt: a lost score is not worth delaying the result.</summary>
    public static ActivityOptions Telemetry => new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(1),
        RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
    };

    /// <summary>One Claude Code run. Long, so it must heartbeat.</summary>
    public static ActivityOptions RunAgent => new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(45),
        HeartbeatTimeout = TimeSpan.FromMinutes(2),
        RetryPolicy = new RetryPolicy { MaximumAttempts = 2 },
    };

    /// <summary>Build + tests + locked-path check. Long, so it must heartbeat.</summary>
    public static ActivityOptions Verify => new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(20),
        HeartbeatTimeout = TimeSpan.FromMinutes(2),
        RetryPolicy = new RetryPolicy { MaximumAttempts = 3 },
    };
}
