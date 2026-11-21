using Orchestrator.Contracts;
using Temporalio.Activities;

namespace Orchestrator.Workflows.Tests;

/// <summary>
/// In-memory stand-ins for the authoring activities. ValidateStories and VerifyTests results are scripted;
/// once a script is exhausted the draft is valid.
/// </summary>
public class FakeAuthoringActivities
{
    public static readonly IReadOnlyList<StorySummary> Stories = [new("S1", "Say hello", 1)];

    private readonly Queue<ValidateStoriesResult> validateScript = new();
    private readonly Queue<VerifyTestsResult> verifyScript = new();

    public List<string> Calls { get; } = [];

    public List<PrepareStoriesDraftInput> StoryDrafts { get; } = [];

    public List<CommitSpecInput> Commits { get; } = [];

    public FakeAuthoringActivities ScriptValidate(params ValidateStoriesResult[] results)
    {
        foreach (var result in results)
        {
            validateScript.Enqueue(result);
        }

        return this;
    }

    public FakeAuthoringActivities ScriptVerifyTests(params VerifyTestsResult[] results)
    {
        foreach (var result in results)
        {
            verifyScript.Enqueue(result);
        }

        return this;
    }

    public static ValidateStoriesResult Invalid(params string[] errors) => new(errors, "/work/stories.json", []);

    public static VerifyTestsResult TestsPassing(params string[] passing) =>
        new(false, BuildSucceeded: true, [], [], passing, [], "passing", "tests.log");

    public static VerifyTestsResult BuildFailed() =>
        new(false, BuildSucceeded: false, [], [], [], [], "build-failed", "tests.log");

    [Activity(ActivityNames.ImportSpec)]
    public void ImportSpec(ImportSpecInput input) => Record(ActivityNames.ImportSpec, input.SpecFilePath);

    [Activity(ActivityNames.PrepareStoriesDraft)]
    public void PrepareStoriesDraft(PrepareStoriesDraftInput input)
    {
        Record(ActivityNames.PrepareStoriesDraft, input.Draft.ToString());
        StoryDrafts.Add(input);
    }

    [Activity(ActivityNames.ValidateStories)]
    public ValidateStoriesResult ValidateStories(ValidateStoriesInput input)
    {
        Record(ActivityNames.ValidateStories, input.FeatureId);
        return validateScript.Count > 0 ? validateScript.Dequeue() : new ValidateStoriesResult([], "/work/stories.json", Stories);
    }

    [Activity(ActivityNames.PrepareTestsDraft)]
    public void PrepareTestsDraft(PrepareTestsDraftInput input) => Record(ActivityNames.PrepareTestsDraft, input.Draft.ToString());

    [Activity(ActivityNames.VerifyTests)]
    public VerifyTestsResult VerifyTests(VerifyTestsInput input)
    {
        Record(ActivityNames.VerifyTests, input.Draft.ToString());
        return verifyScript.Count > 0
            ? verifyScript.Dequeue()
            : new VerifyTestsResult(true, true, [], [], [], [], null, "tests.log");
    }

    [Activity(ActivityNames.CommitSpec)]
    public CommitSpecResult CommitSpec(CommitSpecInput input)
    {
        Record(ActivityNames.CommitSpec, input.Stage.ToString());
        Commits.Add(input);
        return new CommitSpecResult($"sha-{input.Stage}");
    }

    public IEnumerable<string> CallsTo(string activityName) =>
        Calls.Where(c => c.StartsWith(activityName + ":", StringComparison.Ordinal));

    private void Record(string activity, string detail)
    {
        lock (Calls)
        {
            Calls.Add($"{activity}:{detail}");
        }
    }
}
