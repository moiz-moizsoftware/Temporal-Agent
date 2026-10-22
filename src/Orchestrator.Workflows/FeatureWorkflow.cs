using Orchestrator.Contracts;
using Temporalio.Workflows;

namespace Orchestrator.Workflows;

/// <summary>
/// Drives one feature through the Ralph loop: per iteration RunAgent → Verify → (CommitStory | SummarizeFailure).
/// Story status lives here in workflow state; status.json is only a projection written by an activity.
/// </summary>
[Workflow]
public class FeatureWorkflow
{
    private readonly List<StoryStatus> statuses = [];
    private string? branchName;
    private string? lastSummaryPath;
    private string? lastRawLogPath;

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
                new VerifyInput(worktree, input.FeatureId, story.Id, iteration, story.TestIds),
                FeatureWorkflowTimeouts.Verify);

            if (verify.Passed)
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
                new SummarizeFailureInput(worktree, input.FeatureId, story.Id, iteration, verify.RawLogPath),
                FeatureWorkflowTimeouts.Short);
            lastSummaryPath = summary.SummaryPath;
            lastRawLogPath = verify.RawLogPath;

            var (updated, escalation) = RalphLoopPolicy.ApplyFailure(
                GetStatus(story.Id), verify.FailureSignature, input.MaxAttemptsPerStory);
            SetStatus(updated);

            if (escalation is { } reason)
            {
                return Escalate(reason, story.Id);
            }
        }

        return new FeatureWorkflowResult(FeatureOutcome.Completed, statuses.ToList(), Escalation: null, branchName);
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
