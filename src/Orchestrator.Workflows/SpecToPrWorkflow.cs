using Microsoft.Extensions.Logging;
using Orchestrator.Contracts;
using Temporalio.Workflows;

namespace Orchestrator.Workflows;

/// <summary>
/// Takes only a spec and drives it to an implemented feature: the agent drafts stories.json, a human approves it
/// (ApproveStories signal), the agent drafts failing acceptance tests that are verified mechanically, both are
/// committed on spec/{featureId}, and then FeatureWorkflow implements the stories as a child workflow.
/// </summary>
[Workflow]
public class SpecToPrWorkflow
{
    private StoriesDecision? storiesDecision;

    /// <summary>Where the run is. Shown by `temporal workflow query --name Stage`.</summary>
    [WorkflowQuery]
    public SpecToPrStage Stage { get; private set; } = SpecToPrStage.Preparing;

    /// <summary>The stories draft waiting for a decision, or null. Shown by `temporal workflow query`.</summary>
    [WorkflowQuery]
    public StoriesApprovalRequest? PendingStoriesApproval { get; private set; }

    [WorkflowRun]
    public async Task<SpecToPrResult> RunAsync(SpecToPrInput input)
    {
        try
        {
            return await RunStagesAsync(input);
        }
        catch (Exception ex) when (UnexpectedFailure.ShouldExplain(ex))
        {
            throw await UnexpectedFailure.ExplainAsync(ex, input.FeatureId);
        }
    }

    private async Task<SpecToPrResult> RunStagesAsync(SpecToPrInput input)
    {
        var workspace = await ExecuteAsync<PrepareWorkspaceResult>(
            ActivityNames.PrepareWorkspace,
            new PrepareWorkspaceInput(
                input.RepoPath, input.BaseCommitSha, input.FeatureId, Workflow.Info.WorkflowId,
                SpecToPrPolicy.SpecBranch(input.FeatureId)),
            FeatureWorkflowTimeouts.Short);
        var worktree = workspace.WorktreePath;

        await Workflow.ExecuteActivityAsync(
            ActivityNames.ImportSpec,
            [new ImportSpecInput(worktree, input.FeatureId, input.SpecFilePath)],
            FeatureWorkflowTimeouts.Short);

        // Stories: draft → validate → human approval, until approved or out of drafts.
        IReadOnlyList<StorySummary>? stories = null;
        string? feedback = null;
        var storyDrafts = 0;
        while (stories is null && storyDrafts < input.MaxStoryDrafts)
        {
            storyDrafts++;
            Stage = SpecToPrStage.DraftingStories;
            await Workflow.ExecuteActivityAsync(
                ActivityNames.PrepareStoriesDraft,
                [new PrepareStoriesDraftInput(worktree, input.FeatureId, storyDrafts, feedback)],
                FeatureWorkflowTimeouts.Short);
            await RunAgentAsync(worktree, input.FeatureId, DraftIds.Stories, storyDrafts, AgentProfile.DraftStories);

            var validation = await ExecuteAsync<ValidateStoriesResult>(
                ActivityNames.ValidateStories,
                new ValidateStoriesInput(worktree, input.FeatureId),
                FeatureWorkflowTimeouts.Short);
            if (validation.Errors.Count > 0)
            {
                feedback = SpecToPrPolicy.InvalidStoriesFeedback(validation.Errors);
                continue;
            }

            var decision = await RequestStoriesApprovalAsync(
                new StoriesApprovalRequest(storyDrafts, validation.StoriesPath, validation.Stories));
            if (decision.Approved)
            {
                stories = validation.Stories;
            }
            else
            {
                feedback = SpecToPrPolicy.RejectedStoriesFeedback(decision.Comment);
            }
        }

        if (stories is null)
        {
            return Escalate([], null, new SpecToPrEscalation(SpecEscalationReason.StoriesNotApproved, storyDrafts, feedback));
        }

        await ExecuteAsync<CommitSpecResult>(
            ActivityNames.CommitSpec,
            new CommitSpecInput(worktree, input.FeatureId, SpecCommitStage.Stories),
            FeatureWorkflowTimeouts.Short);

        // Tests: draft → verify (builds, every story test found and failing, nothing else broken), until valid.
        var testsValid = false;
        string? lastSummaryPath = null;
        var testDrafts = 0;
        while (!testsValid && testDrafts < input.MaxTestDrafts)
        {
            testDrafts++;
            Stage = SpecToPrStage.DraftingTests;
            await Workflow.ExecuteActivityAsync(
                ActivityNames.PrepareTestsDraft,
                [new PrepareTestsDraftInput(worktree, input.FeatureId, testDrafts)],
                FeatureWorkflowTimeouts.Short);
            await RunAgentAsync(worktree, input.FeatureId, DraftIds.Tests, testDrafts, AgentProfile.DraftTests);

            var verify = await ExecuteAsync<VerifyTestsResult>(
                ActivityNames.VerifyTests,
                new VerifyTestsInput(worktree, input.FeatureId, testDrafts),
                FeatureWorkflowTimeouts.Verify);
            testsValid = verify.Passed;
            if (!testsValid)
            {
                var summary = await ExecuteAsync<SummarizeFailureResult>(
                    ActivityNames.SummarizeFailure,
                    new SummarizeFailureInput(
                        worktree, input.FeatureId, DraftIds.Tests, testDrafts, verify.RawLogPath, SpecToPrPolicy.TestsFeedback(verify)),
                    FeatureWorkflowTimeouts.Short);
                lastSummaryPath = summary.SummaryPath;
            }
        }

        if (!testsValid)
        {
            return Escalate(stories, null, new SpecToPrEscalation(SpecEscalationReason.TestsInvalid, testDrafts, lastSummaryPath));
        }

        var specCommit = await ExecuteAsync<CommitSpecResult>(
            ActivityNames.CommitSpec,
            new CommitSpecInput(worktree, input.FeatureId, SpecCommitStage.Tests),
            FeatureWorkflowTimeouts.Short);

        // Implementation: the existing Ralph loop, on ralph/{featureId} branched from the spec commit.
        Stage = SpecToPrStage.Implementing;
        var feature = await Workflow.ExecuteChildWorkflowAsync(
            (FeatureWorkflow wf) => wf.RunAsync(new FeatureWorkflowInput(
                input.RepoPath,
                $"specs/{input.FeatureId}/spec.md",
                specCommit.CommitSha,
                input.FeatureId,
                input.MaxAttemptsPerStory,
                input.MaxTotalIterations)),
            new ChildWorkflowOptions { Id = SpecToPrPolicy.ImplementationWorkflowId(Workflow.Info.WorkflowId) });

        Stage = SpecToPrStage.Done;
        return feature.Outcome == FeatureOutcome.Completed
            ? new SpecToPrResult(SpecToPrOutcome.Completed, stories, specCommit.CommitSha, Escalation: null, feature)
            : new SpecToPrResult(
                SpecToPrOutcome.Escalated,
                stories,
                specCommit.CommitSha,
                new SpecToPrEscalation(SpecEscalationReason.ImplementationEscalated, testDrafts, feature.Escalation?.LastFailureSummaryPath),
                feature);
    }

    /// <summary>Approves or rejects the pending stories draft. Ignored when nothing is waiting.</summary>
    [WorkflowSignal]
    public Task ApproveStoriesAsync(StoriesDecision decision)
    {
        if (PendingStoriesApproval is not null)
        {
            storiesDecision = decision;
        }

        return Task.CompletedTask;
    }

    /// <summary>Waits, durably and without a deadline, for a human decision on a valid stories draft.</summary>
    private async Task<StoriesDecision> RequestStoriesApprovalAsync(StoriesApprovalRequest request)
    {
        Stage = SpecToPrStage.AwaitingStoriesApproval;
        PendingStoriesApproval = request;
        storiesDecision = null;
        Workflow.Logger.LogWarning(
            "Stories draft {Draft} ({Count} stories) is ready at {Path}; waiting for the ApproveStories signal.",
            request.Draft, request.Stories.Count, request.StoriesPath);

        await Workflow.WaitConditionAsync(() => storiesDecision is not null);
        var decision = storiesDecision!;
        PendingStoriesApproval = null;
        storiesDecision = null;
        return decision;
    }

    private SpecToPrResult Escalate(IReadOnlyList<StorySummary> stories, string? specCommitSha, SpecToPrEscalation escalation)
    {
        Stage = SpecToPrStage.Done;
        Workflow.Logger.LogWarning("Escalated: {Reason} after {Drafts} drafts.", escalation.Reason, escalation.Drafts);
        return new SpecToPrResult(SpecToPrOutcome.Escalated, stories, specCommitSha, escalation, Feature: null);
    }

    private static Task<RunAgentResult> RunAgentAsync(
        string worktree, string featureId, string draftId, int draft, AgentProfile profile) =>
        ExecuteAsync<RunAgentResult>(
            ActivityNames.RunAgent,
            new RunAgentInput(worktree, featureId, draftId, draft, profile),
            FeatureWorkflowTimeouts.RunAgent);

    private static Task<TResult> ExecuteAsync<TResult>(string activityName, object input, ActivityOptions options) =>
        Workflow.ExecuteActivityAsync<TResult>(activityName, [input], options);
}
