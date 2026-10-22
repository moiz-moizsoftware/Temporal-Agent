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
