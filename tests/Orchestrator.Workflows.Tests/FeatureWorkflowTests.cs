using Orchestrator.Contracts;
using Temporalio.Client;
using Temporalio.Worker;

namespace Orchestrator.Workflows.Tests;

public class FeatureWorkflowTests(WorkflowEnvironmentFixture fixture) : IClassFixture<WorkflowEnvironmentFixture>
{
    private static readonly StoryRef S1 = new("S1", Priority: 1, ["Hello.Tests.S1Tests.Works"]);
    private static readonly StoryRef S2 = new("S2", Priority: 2, ["Hello.Tests.S2Tests.Works"]);

    [Fact]
    public async Task HappyPath_CommitsEachStoryInPriorityOrder()
    {
        var fake = new FakeActivities(S2, S1);

        var result = await RunAsync(fake);

        Assert.Equal(FeatureOutcome.Completed, result.Outcome);
        Assert.Null(result.Escalation);
        Assert.Equal("ralph/f1", result.BranchName);
        Assert.All(result.Stories, s => Assert.True(s.Passes));
        Assert.Equal(["CommitStory:S1", "CommitStory:S2"], fake.CallsTo(ActivityNames.CommitStory));
        Assert.Empty(fake.CallsTo(ActivityNames.SummarizeFailure));
    }

    [Fact]
    public async Task RetryThenPass_SummarizesFailureThenCommits()
    {
        var fake = new FakeActivities(S1).ScriptVerify("S1", FakeActivities.Fail("sig-a"));

        var result = await RunAsync(fake);

        Assert.Equal(FeatureOutcome.Completed, result.Outcome);
        Assert.Equal(new StoryStatus("S1", Passes: true, Attempts: 1, "sig-a"), result.Stories.Single());
        Assert.Equal(["SummarizeFailure:S1:1"], fake.CallsTo(ActivityNames.SummarizeFailure));
        Assert.Equal(["CommitStory:S1"], fake.CallsTo(ActivityNames.CommitStory));
        // The second iteration's status.json projection reflects the failed attempt.
        Assert.Equal(1, fake.LoopFileWrites[1].Statuses.Single().Attempts);
    }

    [Fact]
    public async Task RepeatedDistinctFailures_EscalateAtAttemptCap()
    {
        var fake = new FakeActivities(S1).ScriptVerify(
            "S1", FakeActivities.Fail("sig-a"), FakeActivities.Fail("sig-b"), FakeActivities.Fail("sig-c"));

        var result = await RunAsync(fake, maxAttemptsPerStory: 3);

        Assert.Equal(FeatureOutcome.Escalated, result.Outcome);
        Assert.Equal(
            new EscalationInfo(EscalationReason.MaxAttemptsPerStory, "S1", 3, "last_failure_3.md", "raw.log"),
            result.Escalation);
        Assert.Equal(3, fake.CallsTo(ActivityNames.RunAgent).Count());
        Assert.Empty(fake.CallsTo(ActivityNames.CommitStory));
    }

    [Fact]
    public async Task SameFailureSignatureTwiceInARow_EscalatesAsStalled()
    {
        var fake = new FakeActivities(S1).ScriptVerify("S1", FakeActivities.Fail("sig-a"), FakeActivities.Fail("sig-a"));

        var result = await RunAsync(fake, maxAttemptsPerStory: 5);

        Assert.Equal(FeatureOutcome.Escalated, result.Outcome);
        Assert.Equal(EscalationReason.Stalled, result.Escalation!.Reason);
        Assert.Equal(2, result.Escalation.Attempts);
        Assert.Equal(2, fake.CallsTo(ActivityNames.RunAgent).Count());
    }

    [Fact]
    public async Task LockedFileViolation_IsNotCommittedAndTakesFailurePath()
    {
        var violation = new VerifyResult(
            Passed: false, BuildSucceeded: true, StoryTestsPassed: true, FullSuitePassed: true,
            ["specs/f1/spec.md"], "locked:specs/f1/spec.md", "raw.log");
        var fake = new FakeActivities(S1).ScriptVerify("S1", violation);

        var result = await RunAsync(fake);

        Assert.Equal(
            ["Verify:S1:1", "SummarizeFailure:S1:1", "Verify:S1:2", "CommitStory:S1"],
            fake.Calls.Where(c => c.StartsWith("Verify") || c.StartsWith("SummarizeFailure") || c.StartsWith("CommitStory")));
        Assert.Equal(FeatureOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.Stories.Single().Attempts);
    }

    [Fact]
    public async Task TotalIterationCap_Escalates()
    {
        var fake = new FakeActivities(S1, S2).ScriptVerify(
            "S1", FakeActivities.Fail("sig-a"), FakeActivities.Fail("sig-b"), FakeActivities.Fail("sig-c"));

        var result = await RunAsync(fake, maxAttemptsPerStory: 10, maxTotalIterations: 2);

        Assert.Equal(FeatureOutcome.Escalated, result.Outcome);
        Assert.Equal(
            new EscalationInfo(EscalationReason.MaxTotalIterations, "S1", 2, "last_failure_2.md", "raw.log"),
            result.Escalation);
        Assert.Equal(2, fake.CallsTo(ActivityNames.RunAgent).Count());
    }

    private async Task<FeatureWorkflowResult> RunAsync(
        FakeActivities fake, int maxAttemptsPerStory = 3, int maxTotalIterations = 20)
    {
        var taskQueue = $"test-{Guid.NewGuid()}";
        var input = new FeatureWorkflowInput(
            "/repo", "specs/f1/spec.md", "abc123", "f1", maxAttemptsPerStory, maxTotalIterations);

        using var worker = new TemporalWorker(
            fixture.Env.Client,
            new TemporalWorkerOptions(taskQueue).AddWorkflow<FeatureWorkflow>().AddAllActivities(fake));

        return await worker.ExecuteAsync(() => fixture.Env.Client.ExecuteWorkflowAsync(
            (FeatureWorkflow wf) => wf.RunAsync(input),
            new WorkflowOptions($"feature-f1-{Guid.NewGuid()}", taskQueue)));
    }
}
