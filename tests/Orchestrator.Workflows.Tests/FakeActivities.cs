using Orchestrator.Contracts;
using Temporalio.Activities;

namespace Orchestrator.Workflows.Tests;

/// <summary>
/// In-memory stand-ins registered under the real activity names. Verify results are scripted per story;
/// once a story's script is exhausted it passes.
/// </summary>
public class FakeActivities
{
    private readonly Dictionary<string, Queue<VerifyResult>> verifyScripts = [];

    public FakeActivities(params StoryRef[] stories) => Stories = stories;

    public IReadOnlyList<StoryRef> Stories { get; }

    public List<string> Calls { get; } = [];

    public List<PrepareWorkspaceInput> WorkspaceInputs { get; } = [];

    public List<RunAgentInput> AgentInputs { get; } = [];

    public List<WriteLoopFilesInput> LoopFileWrites { get; } = [];

    public List<VerifyInput> VerifyInputs { get; } = [];

    public List<SummarizeFailureInput> SummarizeInputs { get; } = [];

    public List<RecordFeatureOutcomeInput> Outcomes { get; } = [];

    public FakeActivities ScriptVerify(string storyId, params VerifyResult[] results)
    {
        verifyScripts[storyId] = new Queue<VerifyResult>(results);
        return this;
    }

    public static VerifyResult Fail(string signature) =>
        new(Passed: false, BuildSucceeded: true, StoryTestsPassed: false, FullSuitePassed: false, [], signature, "raw.log", []);

    public static VerifyResult PassWithPackages(params string[] packages) =>
        new(Passed: true, BuildSucceeded: true, StoryTestsPassed: true, FullSuitePassed: true, [], null, "raw.log", packages);

    public IEnumerable<string> CallsTo(string activityName) =>
        Calls.Where(c => c.StartsWith(activityName + ":", StringComparison.Ordinal));

    [Activity(ActivityNames.PrepareWorkspace)]
    public PrepareWorkspaceResult PrepareWorkspace(PrepareWorkspaceInput input)
    {
        Record(ActivityNames.PrepareWorkspace, input.FeatureId);
        lock (WorkspaceInputs)
        {
            WorkspaceInputs.Add(input);
        }

        return new PrepareWorkspaceResult($"/work/{input.WorkflowId}", input.BranchName ?? $"ralph/{input.FeatureId}");
    }

    [Activity(ActivityNames.LoadStories)]
    public LoadStoriesResult LoadStories(LoadStoriesInput input)
    {
        Record(ActivityNames.LoadStories, input.FeatureId);
        return new LoadStoriesResult(Stories);
    }

    [Activity(ActivityNames.WriteLoopFiles)]
    public void WriteLoopFiles(WriteLoopFilesInput input)
    {
        Record(ActivityNames.WriteLoopFiles, $"{input.StoryId}:{input.Iteration}");
        LoopFileWrites.Add(input);
    }

    [Activity(ActivityNames.RunAgent)]
    public RunAgentResult RunAgent(RunAgentInput input)
    {
        Record(ActivityNames.RunAgent, $"{input.StoryId}:{input.Iteration}");
        lock (AgentInputs)
        {
            AgentInputs.Add(input);
        }

        return new RunAgentResult(0, Turns: 5, CostUsd: 0.1m, InputTokens: 100, OutputTokens: 50, TimeSpan.FromSeconds(30), "agent.json");
    }

    [Activity(ActivityNames.Verify)]
    public VerifyResult Verify(VerifyInput input)
    {
        Record(ActivityNames.Verify, $"{input.StoryId}:{input.Iteration}");
        VerifyInputs.Add(input);
        return verifyScripts.TryGetValue(input.StoryId, out var script) && script.Count > 0
            ? script.Dequeue()
            : new VerifyResult(true, true, true, true, [], null, "raw.log", []);
    }

    /// <summary>When set, CommitStory fails with this message (non-retryable), as an unexpected git failure would.</summary>
    public string? CommitFailure { get; set; }

    /// <summary>When set, ExplainFailure itself fails, as it would if the worker could not run it.</summary>
    public bool ExplainFails { get; set; }

    public List<ExplainFailureInput> ExplainInputs { get; } = [];

    [Activity(ActivityNames.CommitStory)]
    public CommitStoryResult CommitStory(CommitStoryInput input)
    {
        Record(ActivityNames.CommitStory, input.StoryId);
        return CommitFailure is { } message
            ? throw new Temporalio.Exceptions.ApplicationFailureException(message, errorType: "InvalidOperationException", nonRetryable: true)
            : new CommitStoryResult($"sha-{input.StoryId}");
    }

    [Activity(ActivityNames.ExplainFailure)]
    public ExplainFailureResult ExplainFailure(ExplainFailureInput input)
    {
        Record(ActivityNames.ExplainFailure, input.FailedStep);
        ExplainInputs.Add(input);
        return ExplainFails
            ? throw new Temporalio.Exceptions.ApplicationFailureException("worker down", nonRetryable: true)
            : new ExplainFailureResult($"Explained: {input.FailedStep}", UsedFallback: false);
    }

    [Activity(ActivityNames.SummarizeFailure)]
    public SummarizeFailureResult SummarizeFailure(SummarizeFailureInput input)
    {
        Record(ActivityNames.SummarizeFailure, $"{input.StoryId}:{input.Iteration}");
        SummarizeInputs.Add(input);
        return new SummarizeFailureResult($"last_failure_{input.Iteration}.md", UsedFallback: false);
    }

    [Activity(ActivityNames.RecordFeatureOutcome)]
    public void RecordFeatureOutcome(RecordFeatureOutcomeInput input)
    {
        Record(ActivityNames.RecordFeatureOutcome, input.Outcome.ToString());
        Outcomes.Add(input);
    }

    private void Record(string activity, string detail)
    {
        lock (Calls)
        {
            Calls.Add($"{activity}:{detail}");
        }
    }
}
