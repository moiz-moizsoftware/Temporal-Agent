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
        Assert.Equal(
            new RecordFeatureOutcomeInput("f1", FeatureOutcome.Completed, Reason: null, Iterations: 2, StoriesPassed: 2, StoriesTotal: 2),
            fake.Outcomes.Single());
    }

    [Fact]
    public async Task Verify_TreatsLaterStoriesTestsAsPending()
    {
        var fake = new FakeActivities(S1, S2);

        await RunAsync(fake);

        Assert.Equal(["Hello.Tests.S2Tests.Works"], fake.VerifyInputs[0].PendingTestIds);
        Assert.Empty(fake.VerifyInputs[1].PendingTestIds);
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
        Assert.Equal(
            new RecordFeatureOutcomeInput(
                "f1", FeatureOutcome.Escalated, EscalationReason.MaxAttemptsPerStory, Iterations: 3, StoriesPassed: 0, StoriesTotal: 1),
            fake.Outcomes.Single());
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
            ["specs/f1/spec.md"], "locked:specs/f1/spec.md", "raw.log", []);
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
        Assert.Equal(EscalationReason.MaxTotalIterations, fake.Outcomes.Single().Reason);
    }

    [Fact]
    public async Task NewPackages_ApprovedAreCommitted()
    {
        var fake = new FakeActivities(S1).ScriptVerify("S1", FakeActivities.PassWithPackages("Polly@8.5.0"));
        PackageApprovalRequest? seen = null;

        var result = await RunAsync(fake, decide: async handle =>
        {
            seen = await WaitForApprovalAsync(handle);
            await handle.SignalAsync(wf => wf.ApprovePackagesAsync(new PackageDecision(Approved: true)));
        });

        Assert.Equal(("S1", 1), (seen!.StoryId, seen.Iteration));
        Assert.Equal(["Polly@8.5.0"], seen.Packages);
        Assert.Equal(FeatureOutcome.Completed, result.Outcome);
        Assert.Equal(["CommitStory:S1"], fake.CallsTo(ActivityNames.CommitStory));
        Assert.Empty(fake.CallsTo(ActivityNames.SummarizeFailure));
    }

    [Fact]
    public async Task NewPackages_RejectedTakeFailurePathWithFeedback()
    {
        var fake = new FakeActivities(S1).ScriptVerify("S1", FakeActivities.PassWithPackages("Polly@8.5.0"));

        var result = await RunAsync(fake, decide: async handle =>
        {
            await WaitForApprovalAsync(handle);
            await handle.SignalAsync(wf => wf.ApprovePackagesAsync(new PackageDecision(false, "Use a simple retry loop.")));
        });

        Assert.Equal(["SummarizeFailure:S1:1"], fake.CallsTo(ActivityNames.SummarizeFailure));
        Assert.Contains("Use a simple retry loop.", fake.SummarizeInputs.Single().ReviewerFeedback);
        Assert.Equal(new StoryStatus("S1", Passes: true, Attempts: 1, "packages-rejected:Polly@8.5.0"), result.Stories.Single());
        Assert.Equal(["CommitStory:S1"], fake.CallsTo(ActivityNames.CommitStory)); // second iteration had no new packages
    }

    private static async Task<PackageApprovalRequest> WaitForApprovalAsync(WorkflowHandle<FeatureWorkflow> handle)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await handle.QueryAsync(wf => wf.PendingPackageApproval) is { } pending)
            {
                return pending;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The workflow never asked for package approval.");
    }

    [Fact]
    public async Task UnexpectedActivityFailure_FailsWithExplanationAndKeepsTheCause()
    {
        var fake = new FakeActivities(S1) { CommitFailure = "git add -A failed (128): error: unable to index file 'nul'" };

        var ex = await Assert.ThrowsAsync<Temporalio.Exceptions.WorkflowFailedException>(() => RunAsync(fake));

        var failure = Assert.IsType<Temporalio.Exceptions.ApplicationFailureException>(ex.InnerException);
        Assert.Equal("UnexpectedFailure", failure.ErrorType);
        Assert.Contains("CommitStory failed and stopped the workflow.", failure.Message);
        Assert.Contains("Explained: CommitStory", failure.Message);
        Assert.Contains("unable to index file 'nul'", fake.ExplainInputs.Single().Details);
        Assert.Contains("unable to index file 'nul'", failure.InnerException!.InnerException!.Message); // original cause kept
    }

    [Fact]
    public async Task UnexpectedFailure_WhenExplainingFails_ShowsAddCreditsMessageAndStackTrace()
    {
        var fake = new FakeActivities(S1) { CommitFailure = "git add -A failed (128)", ExplainFails = true };

        var ex = await Assert.ThrowsAsync<Temporalio.Exceptions.WorkflowFailedException>(() => RunAsync(fake));

        Assert.Contains(FailureMessages.AddCredits, ex.InnerException!.Message);
        Assert.Contains("full stack trace", ex.InnerException.Message);
        Assert.Contains("git add -A failed (128)", ex.InnerException.Message);
    }

    private async Task<FeatureWorkflowResult> RunAsync(
        FakeActivities fake,
        int maxAttemptsPerStory = 3,
        int maxTotalIterations = 20,
        Func<WorkflowHandle<FeatureWorkflow>, Task>? decide = null)
    {
        var taskQueue = $"test-{Guid.NewGuid()}";
        var input = new FeatureWorkflowInput(
            "/repo", "specs/f1/spec.md", "abc123", "f1", maxAttemptsPerStory, maxTotalIterations);

        using var worker = new TemporalWorker(
            fixture.Env.Client,
            new TemporalWorkerOptions(taskQueue).AddWorkflow<FeatureWorkflow>().AddAllActivities(fake));

        return await worker.ExecuteAsync(async () =>
        {
            var handle = await fixture.Env.Client.StartWorkflowAsync(
                (FeatureWorkflow wf) => wf.RunAsync(input),
                new WorkflowOptions($"feature-f1-{Guid.NewGuid()}", taskQueue));
            if (decide is not null)
            {
                await decide(handle);
            }

            return await handle.GetResultAsync();
        });
    }
}
