using Orchestrator.Contracts;
using Temporalio.Client;
using Temporalio.Worker;

namespace Orchestrator.Workflows.Tests;

public class SpecToPrWorkflowTests(WorkflowEnvironmentFixture fixture) : IClassFixture<WorkflowEnvironmentFixture>
{
    private static readonly StoryRef S1 = new("S1", Priority: 1, ["Hello.Tests.S1Tests.Works"]);
    private static readonly StoriesDecision Approve = new(Approved: true);

    [Fact]
    public async Task HappyPath_DraftsApprovesTestsCommitsThenImplementsFromSpecCommit()
    {
        var (feature, authoring) = Fakes();

        var (result, workflowId) = await RunAsync(feature, authoring, Approve);

        Assert.Equal(SpecToPrOutcome.Completed, result.Outcome);
        Assert.Null(result.Escalation);
        Assert.Equal("sha-Tests", result.SpecCommitSha);
        Assert.Equal(FakeAuthoringActivities.Stories, result.Stories);
        Assert.Equal(FeatureOutcome.Completed, result.Feature!.Outcome);
        Assert.Equal([SpecCommitStage.Stories, SpecCommitStage.Tests], authoring.Commits.Select(c => c.Stage));
        Assert.Equal(["ImportSpec:/specs/f3.md"], authoring.CallsTo(ActivityNames.ImportSpec));

        // The spec phase works on spec/f3; the child branches ralph/f3 from the tests commit.
        var spec = feature.WorkspaceInputs.Single(w => w.WorkflowId == workflowId);
        Assert.Equal(("spec/f3", "base-sha"), (spec.BranchName, spec.BaseCommitSha));
        var impl = feature.WorkspaceInputs.Single(w => w.WorkflowId == $"{workflowId}-impl");
        Assert.Equal((null, "sha-Tests"), (impl.BranchName, impl.BaseCommitSha));

        Assert.Equal(
            [AgentProfile.DraftStories, AgentProfile.DraftTests, AgentProfile.Implement],
            feature.AgentInputs.Select(a => a.Profile));
        Assert.Equal([DraftIds.Stories, DraftIds.Tests, "S1"], feature.AgentInputs.Select(a => a.StoryId));
    }

    [Fact]
    public async Task InvalidStories_AreRedraftedWithTheErrorsAsFeedback()
    {
        var (feature, authoring) = Fakes();
        authoring.ScriptValidate(FakeAuthoringActivities.Invalid("story S1 has no title."));

        var (result, _) = await RunAsync(feature, authoring, Approve);

        Assert.Equal(SpecToPrOutcome.Completed, result.Outcome);
        Assert.Equal(2, authoring.StoryDrafts.Count);
        Assert.Null(authoring.StoryDrafts[0].Feedback);
        Assert.Contains("story S1 has no title.", authoring.StoryDrafts[1].Feedback);
    }

    [Fact]
    public async Task RejectedStories_AreRedraftedWithTheReviewerComment()
    {
        var (feature, authoring) = Fakes();

        var (result, _) = await RunAsync(feature, authoring, new StoriesDecision(false, "Split S1 into read and write."), Approve);

        Assert.Equal(SpecToPrOutcome.Completed, result.Outcome);
        Assert.Equal(2, authoring.StoryDrafts.Count);
        Assert.Contains("Split S1 into read and write.", authoring.StoryDrafts[1].Feedback);
        Assert.Equal([SpecCommitStage.Stories, SpecCommitStage.Tests], authoring.Commits.Select(c => c.Stage));
    }

    [Fact]
    public async Task StoriesNeverApproved_EscalateWithoutCommittingOrImplementing()
    {
        var (feature, authoring) = Fakes();

        var (result, _) = await RunAsync(
            feature, authoring, maxStoryDrafts: 2, decisions: [new StoriesDecision(false, "No."), new StoriesDecision(false, "Still no.")]);

        Assert.Equal(SpecToPrOutcome.Escalated, result.Outcome);
        Assert.Equal(SpecEscalationReason.StoriesNotApproved, result.Escalation!.Reason);
        Assert.Equal(2, result.Escalation.Drafts);
        Assert.Contains("Still no.", result.Escalation.Detail);
        Assert.Empty(authoring.Commits);
        Assert.Empty(authoring.CallsTo(ActivityNames.PrepareTestsDraft));
        Assert.Null(result.Feature);
    }

    [Fact]
    public async Task InvalidTests_AreSummarizedAndRedrafted()
    {
        var (feature, authoring) = Fakes();
        authoring.ScriptVerifyTests(FakeAuthoringActivities.TestsPassing("Hello.Tests.S1Tests.Works"), FakeAuthoringActivities.BuildFailed());

        var (result, _) = await RunAsync(feature, authoring, Approve);

        Assert.Equal(SpecToPrOutcome.Completed, result.Outcome);
        Assert.Equal(3, authoring.CallsTo(ActivityNames.VerifyTests).Count());
        var summaries = feature.SummarizeInputs.Where(s => s.StoryId == DraftIds.Tests).ToList();
        Assert.Equal(2, summaries.Count);
        Assert.Contains("Hello.Tests.S1Tests.Works", summaries[0].ReviewerFeedback); // explained by the workflow
        Assert.Null(summaries[1].ReviewerFeedback); // build failure: summarized from the raw log
    }

    [Fact]
    public async Task TestsNeverValid_EscalateAfterStoriesCommit()
    {
        var (feature, authoring) = Fakes();
        authoring.ScriptVerifyTests(FakeAuthoringActivities.BuildFailed(), FakeAuthoringActivities.BuildFailed());

        var (result, _) = await RunAsync(feature, authoring, maxTestDrafts: 2, decisions: [Approve]);

        Assert.Equal(SpecToPrOutcome.Escalated, result.Outcome);
        Assert.Equal(new SpecToPrEscalation(SpecEscalationReason.TestsInvalid, 2, "last_failure_2.md"), result.Escalation);
        Assert.Equal([SpecCommitStage.Stories], authoring.Commits.Select(c => c.Stage));
        Assert.Null(result.Feature);
        Assert.Empty(feature.CallsTo(ActivityNames.LoadStories));
    }

    [Fact]
    public async Task ImplementationEscalation_IsReported()
    {
        var (feature, authoring) = Fakes();
        feature.ScriptVerify("S1", FakeActivities.Fail("sig-a"), FakeActivities.Fail("sig-a"));

        var (result, _) = await RunAsync(feature, authoring, Approve);

        Assert.Equal(SpecToPrOutcome.Escalated, result.Outcome);
        Assert.Equal(SpecEscalationReason.ImplementationEscalated, result.Escalation!.Reason);
        Assert.Equal(EscalationReason.Stalled, result.Feature!.Escalation!.Reason);
        Assert.Equal("sha-Tests", result.SpecCommitSha);
    }

    private static (FakeActivities Feature, FakeAuthoringActivities Authoring) Fakes() => (new FakeActivities(S1), new());

    private Task<(SpecToPrResult Result, string WorkflowId)> RunAsync(
        FakeActivities feature, FakeAuthoringActivities authoring, params StoriesDecision[] decisions) =>
        RunAsync(feature, authoring, maxStoryDrafts: 3, maxTestDrafts: 3, decisions);

    /// <summary>Runs the workflow, answering each stories approval request with the next decision in order.</summary>
    private async Task<(SpecToPrResult Result, string WorkflowId)> RunAsync(
        FakeActivities feature,
        FakeAuthoringActivities authoring,
        int maxStoryDrafts = 3,
        int maxTestDrafts = 3,
        StoriesDecision[]? decisions = null)
    {
        var taskQueue = $"test-{Guid.NewGuid()}";
        var workflowId = $"spec-f3-{Guid.NewGuid()}";
        var input = new SpecToPrInput("/repo", "/specs/f3.md", "base-sha", "f3", maxStoryDrafts, maxTestDrafts);

        using var worker = new TemporalWorker(
            fixture.Env.Client,
            new TemporalWorkerOptions(taskQueue)
                .AddWorkflow<SpecToPrWorkflow>()
                .AddWorkflow<FeatureWorkflow>()
                .AddAllActivities(feature)
                .AddAllActivities(authoring));

        return await worker.ExecuteAsync(async () =>
        {
            var handle = await fixture.Env.Client.StartWorkflowAsync(
                (SpecToPrWorkflow wf) => wf.RunAsync(input), new WorkflowOptions(workflowId, taskQueue));

            var answered = 0;
            foreach (var decision in decisions ?? [])
            {
                var pending = await WaitForApprovalAsync(handle, afterDraft: answered);
                answered = pending.Draft;
                await handle.SignalAsync(wf => wf.ApproveStoriesAsync(decision));
            }

            return (await handle.GetResultAsync(), workflowId);
        });
    }

    private static async Task<StoriesApprovalRequest> WaitForApprovalAsync(WorkflowHandle<SpecToPrWorkflow> handle, int afterDraft)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await handle.QueryAsync(wf => wf.PendingStoriesApproval) is { } pending && pending.Draft > afterDraft)
            {
                return pending;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The workflow never asked for stories approval.");
    }
}
