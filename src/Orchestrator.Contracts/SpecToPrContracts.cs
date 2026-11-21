namespace Orchestrator.Contracts;

/// <summary>
/// Input of <c>SpecToPrWorkflow</c>: only a spec file. Stories and acceptance tests are drafted by the agent,
/// then <c>FeatureWorkflow</c> implements them as a child workflow.
/// </summary>
/// <param name="SpecFilePath">Absolute path to the spec on the worker's disk. Copied to specs/{featureId}/spec.md.</param>
public record SpecToPrInput(
    string RepoPath,
    string SpecFilePath,
    string BaseCommitSha,
    string FeatureId,
    int MaxStoryDrafts = 3,
    int MaxTestDrafts = 3,
    int MaxAttemptsPerStory = 3,
    int MaxTotalIterations = 20);

/// <summary>Stand-ins for a story id in agent runs, log file names and failure summaries while authoring.</summary>
public static class DraftIds
{
    public const string Stories = "STORIES";
    public const string Tests = "TESTS";
}

/// <summary>Which agent prompt and tool set a run uses.</summary>
public enum AgentProfile
{
    Implement,
    DraftStories,
    DraftTests,
}

/// <summary>Where a SpecToPrWorkflow run is. Shown by the Stage query.</summary>
public enum SpecToPrStage
{
    Preparing,
    DraftingStories,
    AwaitingStoriesApproval,
    DraftingTests,
    Implementing,
    Done,
}

public enum SpecToPrOutcome
{
    Completed,
    Escalated,
}

public enum SpecEscalationReason
{
    StoriesNotApproved,
    TestsInvalid,
    ImplementationEscalated,
}

/// <param name="Detail">Last feedback (validation errors or reviewer comment) or the last failure summary path.</param>
public record SpecToPrEscalation(SpecEscalationReason Reason, int Drafts, string? Detail);

public record SpecToPrResult(
    SpecToPrOutcome Outcome,
    IReadOnlyList<StorySummary> Stories,
    string? SpecCommitSha,
    SpecToPrEscalation? Escalation,
    FeatureWorkflowResult? Feature);

/// <summary>What a reviewer sees for one drafted story. Acceptance criteria stay in stories.json.</summary>
public record StorySummary(string Id, string Title, int Priority);

/// <summary>A valid stories draft waiting for a human decision. Shown by the PendingStoriesApproval query.</summary>
public record StoriesApprovalRequest(int Draft, string StoriesPath, IReadOnlyList<StorySummary> Stories);

/// <summary>Payload of the ApproveStories signal. A rejection comment is passed to the next draft.</summary>
public record StoriesDecision(bool Approved, string? Comment = null);

public record ImportSpecInput(string WorktreePath, string FeatureId, string SpecFilePath);

/// <param name="Feedback">Why the previous draft was not accepted; null for the first draft.</param>
public record PrepareStoriesDraftInput(string WorktreePath, string FeatureId, int Draft, string? Feedback);

public record ValidateStoriesInput(string WorktreePath, string FeatureId);

public record ValidateStoriesResult(IReadOnlyList<string> Errors, string StoriesPath, IReadOnlyList<StorySummary> Stories);

public record PrepareTestsDraftInput(string WorktreePath, string FeatureId, int Draft);

public record VerifyTestsInput(string WorktreePath, string FeatureId, int Draft);

/// <param name="PathViolations">Changed files outside specs/{featureId}/acceptance and solution files.</param>
/// <param name="MissingTests">Story test ids that were not found in the test results.</param>
/// <param name="PassingTests">Story test ids that already pass, so they cannot prove the story was implemented.</param>
/// <param name="BrokenTests">Failing tests that are not story test ids (existing tests broken, or extra tests).</param>
public record VerifyTestsResult(
    bool Passed,
    bool BuildSucceeded,
    IReadOnlyList<string> PathViolations,
    IReadOnlyList<string> MissingTests,
    IReadOnlyList<string> PassingTests,
    IReadOnlyList<string> BrokenTests,
    string? FailureSignature,
    string RawLogPath);

public enum SpecCommitStage
{
    /// <summary>spec.md and the approved stories.json.</summary>
    Stories,

    /// <summary>The verified acceptance tests and any solution file change.</summary>
    Tests,
}

public record CommitSpecInput(string WorktreePath, string FeatureId, SpecCommitStage Stage);

public record CommitSpecResult(string CommitSha);
