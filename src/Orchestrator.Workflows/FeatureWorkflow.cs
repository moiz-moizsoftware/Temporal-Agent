using Microsoft.Extensions.Logging;
using Orchestrator.Contracts;
using Temporalio.Workflows;

namespace Orchestrator.Workflows;

/// <summary>
/// Drives one feature through the Ralph loop: per iteration RunAgent → Verify → (CommitStory | SummarizeFailure).
/// A passing iteration that adds NuGet packages waits for the ApprovePackages signal before it is committed.
/// Story status lives here in workflow state; status.json is only a projection written by an activity.
/// </summary>
[Workflow]
public class FeatureWorkflow
{
    private readonly List<StoryStatus> statuses = [];
    private string? branchName;
    private string? lastSummaryPath;
    private string? lastRawLogPath;
    private PackageDecision? packageDecision;

    [WorkflowRun]
    public async Task<FeatureWorkflowResult> RunAsync(FeatureWorkflowInput input)
    {
        var workspace = await ExecuteAsync<PrepareWorkspaceResult>(
            ActivityNames.PrepareWorkspace,
            new PrepareWorkspaceInput(input.RepoPath, input.BaseCommitSha, input.FeatureId, Workflow.Info.WorkflowId),
            FeatureWorkflowTimeouts.Short);
        branchName = workspace.BranchName;
        var worktree = workspace.WorktreePath;

        var loaded = await ExecuteAsync<LoadStoriesResult>(
            ActivityNames.LoadStories,
            new LoadStoriesInput(worktree, input.FeatureId),
            FeatureWorkflowTimeouts.Short);
        statuses.AddRange(loaded.Stories.Select(s => new StoryStatus(s.Id, Passes: false, Attempts: 0, LastFailureSignature: null)));

        var iteration = 0;
        while (RalphLoopPolicy.PickNext(loaded.Stories, statuses) is { } story)
        {
            if (iteration >= input.MaxTotalIterations)
            {
                return Escalate(EscalationReason.MaxTotalIterations, story.Id);
            }

            iteration++;

            await Workflow.ExecuteActivityAsync(
                ActivityNames.WriteLoopFiles,
                [new WriteLoopFilesInput(worktree, input.FeatureId, story.Id, iteration, statuses.ToList())],
                FeatureWorkflowTimeouts.Short);

            await ExecuteAsync<RunAgentResult>(
                ActivityNames.RunAgent,
                new RunAgentInput(worktree, input.FeatureId, story.Id, iteration),
                FeatureWorkflowTimeouts.RunAgent);

            var verify = await ExecuteAsync<VerifyResult>(
                ActivityNames.Verify,
                new VerifyInput(
                    worktree, input.FeatureId, story.Id, iteration, story.TestIds,
                    RalphLoopPolicy.PendingTestIds(loaded.Stories, statuses, story)),
                FeatureWorkflowTimeouts.Verify);

            var rejection = verify.Passed && verify.NewPackages.Count > 0
                ? await RequestPackageApprovalAsync(new PackageApprovalRequest(story.Id, iteration, verify.NewPackages))
                : null;

            if (verify.Passed && rejection is null)
            {
                await ExecuteAsync<CommitStoryResult>(
                    ActivityNames.CommitStory,
                    new CommitStoryInput(worktree, input.FeatureId, story.Id, iteration),
                    FeatureWorkflowTimeouts.Short);
                SetStatus(GetStatus(story.Id) with { Passes = true });
                continue;
            }

            var summary = await ExecuteAsync<SummarizeFailureResult>(
                ActivityNames.SummarizeFailure,
                new SummarizeFailureInput(
                    worktree, input.FeatureId, story.Id, iteration, verify.RawLogPath,
                    rejection is null ? null : RalphLoopPolicy.PackageRejectionFeedback(verify.NewPackages, rejection.Comment)),
                FeatureWorkflowTimeouts.Short);
            lastSummaryPath = summary.SummaryPath;
            lastRawLogPath = verify.RawLogPath;

            var signature = rejection is null
                ? verify.FailureSignature
                : RalphLoopPolicy.PackageRejectionSignature(verify.NewPackages);
            var (updated, escalation) = RalphLoopPolicy.ApplyFailure(
                GetStatus(story.Id), signature, input.MaxAttemptsPerStory);
            SetStatus(updated);

            if (escalation is { } reason)
            {
                return Escalate(reason, story.Id);
            }
        }

        return new FeatureWorkflowResult(FeatureOutcome.Completed, statuses.ToList(), Escalation: null, branchName);
    }

    /// <summary>The iteration waiting for a package decision, or null. Shown by `temporal workflow query`.</summary>
    [WorkflowQuery]
    public PackageApprovalRequest? PendingPackageApproval { get; private set; }

    /// <summary>Approves or rejects the pending packages. Ignored when nothing is waiting.</summary>
    [WorkflowSignal]
    public Task ApprovePackagesAsync(PackageDecision decision)
    {
        if (PendingPackageApproval is not null)
        {
            packageDecision = decision;
        }

        return Task.CompletedTask;
    }

    /// <summary>Waits, durably and without a deadline, for a human decision. Returns the decision if rejected.</summary>
    private async Task<PackageDecision?> RequestPackageApprovalAsync(PackageApprovalRequest request)
    {
        PendingPackageApproval = request;
        packageDecision = null;
        Workflow.Logger.LogWarning(
            "Story {StoryId} iteration {Iteration} added packages {Packages}; waiting for the ApprovePackages signal.",
            request.StoryId, request.Iteration, string.Join(", ", request.Packages));

        await Workflow.WaitConditionAsync(() => packageDecision is not null);
        var decision = packageDecision!;
        PendingPackageApproval = null;
        packageDecision = null;
        return decision.Approved ? null : decision;
    }

    private FeatureWorkflowResult Escalate(EscalationReason reason, string storyId) =>
        new(
            FeatureOutcome.Escalated,
            statuses.ToList(),
            new EscalationInfo(reason, storyId, GetStatus(storyId).Attempts, lastSummaryPath, lastRawLogPath),
            branchName);

    private StoryStatus GetStatus(string storyId) => statuses.Single(s => s.Id == storyId);

    private void SetStatus(StoryStatus status) => statuses[statuses.FindIndex(s => s.Id == status.Id)] = status;

    private static Task<TResult> ExecuteAsync<TResult>(string activityName, object input, ActivityOptions options) =>
        Workflow.ExecuteActivityAsync<TResult>(activityName, [input], options);
}
