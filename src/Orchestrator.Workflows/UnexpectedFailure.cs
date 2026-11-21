using System.Text;
using Microsoft.Extensions.Logging;
using Orchestrator.Contracts;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Orchestrator.Workflows;

/// <summary>
/// When an activity fails in a way the loop does not handle (retries exhausted), the workflow stops. Before it does,
/// the failure is explained: an LLM summary when API credits are available, otherwise the "please add credits"
/// message plus the full stack trace. The original failure stays attached as the cause, so nothing is lost, and the
/// run can be resumed with `temporal workflow reset` once the cause is fixed.
/// </summary>
internal static class UnexpectedFailure
{
    /// <summary>Patch id: runs that failed before this existed replay without the extra activity.</summary>
    internal const string PatchId = "explain-unexpected-failure";

    private static readonly ActivityOptions ExplainOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(2),
        RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
    };

    /// <summary>Activity failures only; cancellation is deliberate, and a child workflow explains its own failure.</summary>
    public static bool ShouldExplain(Exception ex) =>
        ex is ActivityFailureException && !TemporalException.IsCanceledException(ex);

    /// <summary>The exception to throw: the explained failure, or the original one when replaying an older run.</summary>
    public static async Task<Exception> ExplainAsync(Exception ex, string featureId)
    {
        if (!Workflow.Patched(PatchId))
        {
            return ex;
        }

        var step = (ex as ActivityFailureException)?.ActivityType ?? "unknown step";
        var details = Describe(ex);
        string explanation;
        try
        {
            explanation = (await Workflow.ExecuteActivityAsync<ExplainFailureResult>(
                ActivityNames.ExplainFailure, [new ExplainFailureInput(featureId, step, details)], ExplainOptions)).Explanation;
        }
        catch (ActivityFailureException explainError)
        {
            Workflow.Logger.LogWarning(explainError, "ExplainFailure failed; reporting the raw stack trace.");
            explanation = FailureMessages.WithoutSummary(explainError.InnerException?.Message, "full stack trace", details);
        }

        return new ApplicationFailureException(
            $"{step} failed and stopped the workflow.\n\n{explanation}", ex, errorType: "UnexpectedFailure", nonRetryable: true);
    }

    /// <summary>Every message and stack trace in the failure chain, outermost first.</summary>
    internal static string Describe(Exception ex)
    {
        var text = new StringBuilder();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            text.Append(current.GetType().Name).Append(": ").AppendLine(current.Message);
            if (!string.IsNullOrWhiteSpace(current.StackTrace))
            {
                text.AppendLine(current.StackTrace.TrimEnd());
            }
        }

        return text.ToString().TrimEnd();
    }
}
