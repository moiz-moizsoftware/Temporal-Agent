using Orchestrator.Contracts;

namespace Orchestrator.Workflows;

/// <summary>Pure decision logic for the Ralph loop. No Temporal APIs, no side effects.</summary>
public static class RalphLoopPolicy
{
    /// <summary>Highest-priority story (lowest number) that does not pass yet; ties broken by id.</summary>
    public static StoryRef? PickNext(IReadOnlyList<StoryRef> stories, IReadOnlyList<StoryStatus> statuses) =>
        stories
            .Where(s => !statuses.Single(st => st.Id == s.Id).Passes)
            .OrderBy(s => s.Priority)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>
    /// Tests of other stories that do not pass yet. They are expected to fail, so Verify leaves them out of the
    /// full-suite check. A test shared with the current story is not pending.
    /// </summary>
    public static IReadOnlyList<string> PendingTestIds(
        IReadOnlyList<StoryRef> stories, IReadOnlyList<StoryStatus> statuses, StoryRef current) =>
        stories
            .Where(s => s.Id != current.Id && !statuses.Single(st => st.Id == s.Id).Passes)
            .SelectMany(s => s.TestIds)
            .Except(current.TestIds)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Failure signature for rejected packages; a repeat of the same request stalls the story.</summary>
    public static string PackageRejectionSignature(IReadOnlyList<string> packages) =>
        "packages-rejected:" + string.Join('|', packages.Order(StringComparer.Ordinal));

    /// <summary>The last_failure.md body for rejected packages, written for the agent's next attempt.</summary>
    public static string PackageRejectionFeedback(IReadOnlyList<string> packages, string? comment) =>
        $"""
        ## What failed
        Build and tests passed, but a reviewer rejected the NuGet packages you added: {string.Join(", ", packages)}.

        ## Reviewer comment
        {(string.IsNullOrWhiteSpace(comment) ? "(none)" : comment)}

        ## What to try next
        Remove the rejected packages from the project files and meet the acceptance criteria without them,
        or with an alternative the comment allows.
        """;

    /// <summary>Records a failed attempt and decides whether the story must escalate.</summary>
    public static (StoryStatus Status, EscalationReason? Escalation) ApplyFailure(
        StoryStatus current, string? failureSignature, int maxAttemptsPerStory)
    {
        var updated = current with { Attempts = current.Attempts + 1, LastFailureSignature = failureSignature };

        if (failureSignature is not null && failureSignature == current.LastFailureSignature)
        {
            return (updated, EscalationReason.Stalled);
        }

        if (updated.Attempts >= maxAttemptsPerStory)
        {
            return (updated, EscalationReason.MaxAttemptsPerStory);
        }

        return (updated, null);
    }
}
