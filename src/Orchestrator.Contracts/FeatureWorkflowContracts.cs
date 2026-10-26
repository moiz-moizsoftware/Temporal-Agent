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

/// <summary>A passing iteration that added NuGet packages, waiting for a human decision before it is committed.</summary>
public record PackageApprovalRequest(string StoryId, int Iteration, IReadOnlyList<string> Packages);

/// <summary>Payload of the ApprovePackages signal. A rejection comment is passed to the agent's next attempt.</summary>
public record PackageDecision(bool Approved, string? Comment = null);

public record FeatureWorkflowResult(
    FeatureOutcome Outcome,
    IReadOnlyList<StoryStatus> Stories,
    EscalationInfo? Escalation,
    string? BranchName);
