namespace Orchestrator.Contracts;

/// <param name="BranchName">Branch to create or reuse; null means ralph/{featureId}.</param>
public record PrepareWorkspaceInput(
    string RepoPath, string BaseCommitSha, string FeatureId, string WorkflowId, string? BranchName = null);

public record PrepareWorkspaceResult(string WorktreePath, string BranchName);

public record LoadStoriesInput(string WorktreePath, string FeatureId);

public record LoadStoriesResult(IReadOnlyList<StoryRef> Stories);

public record WriteLoopFilesInput(
    string WorktreePath,
    string FeatureId,
    string StoryId,
    int Iteration,
    IReadOnlyList<StoryStatus> Statuses);

/// <param name="StoryId">The story being implemented, or the draft kind (STORIES, TESTS) when authoring.</param>
public record RunAgentInput(
    string WorktreePath, string FeatureId, string StoryId, int Iteration, AgentProfile Profile = AgentProfile.Implement);

public record RunAgentResult(
    int ExitCode,
    int? Turns,
    decimal? CostUsd,
    long? InputTokens,
    long? OutputTokens,
    TimeSpan Duration,
    string RawOutputPath);

public record VerifyInput(
    string WorktreePath,
    string FeatureId,
    string StoryId,
    int Iteration,
    IReadOnlyList<string> TestIds,
    IReadOnlyList<string> PendingTestIds);

public record VerifyResult(
    bool Passed,
    bool BuildSucceeded,
    bool StoryTestsPassed,
    bool FullSuitePassed,
    IReadOnlyList<string> LockedPathViolations,
    string? FailureSignature,
    string RawLogPath,
    IReadOnlyList<string> NewPackages);

public record CommitStoryInput(string WorktreePath, string FeatureId, string StoryId, int Iteration);

public record CommitStoryResult(string CommitSha);

public record SummarizeFailureInput(
    string WorktreePath,
    string FeatureId,
    string StoryId,
    int Iteration,
    string RawLogPath,
    string? ReviewerFeedback = null);

public record SummarizeFailureResult(string SummaryPath, bool UsedFallback);

public record RecordFeatureOutcomeInput(
    string FeatureId,
    FeatureOutcome Outcome,
    EscalationReason? Reason,
    int Iterations,
    int StoriesPassed,
    int StoriesTotal);
