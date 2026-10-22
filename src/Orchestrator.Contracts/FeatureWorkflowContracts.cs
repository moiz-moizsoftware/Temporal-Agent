namespace Orchestrator.Contracts;

public record FeatureWorkflowInput(
    string RepoPath,
    string SpecPath,
    string BaseCommitSha,
    string FeatureId,
    int MaxAttemptsPerStory = 3,
    int MaxTotalIterations = 20);

public enum FeatureOutcome
{
    Completed,
    Escalated,
}

public enum EscalationReason
{
    MaxAttemptsPerStory,
    MaxTotalIterations,
    Stalled,
}

public record EscalationInfo(
    EscalationReason Reason,
    string StoryId,
    int Attempts,
    string? LastFailureSummaryPath,
    string? RawLogPath);

public record FeatureWorkflowResult(
    FeatureOutcome Outcome,
    IReadOnlyList<StoryStatus> Stories,
    EscalationInfo? Escalation,
    string? BranchName);
