using Microsoft.Extensions.Options;
using Orchestrator.Activities.Agent;
using Orchestrator.Activities.Infrastructure;
using Orchestrator.Activities.Stories;
using Orchestrator.Contracts;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace Orchestrator.Activities.Tests;

public sealed class AuthoringActivitiesTests : IDisposable
{
    private const string FeatureId = "f2";

    private const string ValidStories =
        """
        { "featureId": "f2",
          "stories": [
            { "id": "S2", "title": "Cancel", "priority": 2,
              "acceptanceCriteria": ["Cancels"], "testIds": ["Shop.Acceptance.Tests.CancelTests.Cancels"] },
            { "id": "S1", "title": "Checkout", "priority": 1,
              "acceptanceCriteria": ["Checks out"], "testIds": ["Shop.Acceptance.Tests.CheckoutTests.ChecksOut"] } ] }
        """;

    private readonly TempGitRepo repo = new();
    private readonly ActivityEnvironment env = new();
    private readonly AuthoringActivities authoring = new();
    private readonly string specFile;
    private readonly string worktree;
    private readonly RalphPaths paths;

    public AuthoringActivitiesTests()
    {
        specFile = Path.Combine(repo.Root, "incoming-spec.md");
        File.WriteAllText(specFile, "# Checkout spec\n");
        var workspace = new WorkspaceActivities(Options.Create(new OrchestratorOptions { WorkRoot = repo.WorkRoot }));
        worktree = env.RunAsync(() => workspace.PrepareWorkspaceAsync(
            new PrepareWorkspaceInput(repo.RepoPath, repo.BaseSha, FeatureId, "spec-f2", BranchName: "spec/f2"))).Result.WorktreePath;
        paths = new RalphPaths(worktree, FeatureId);
    }

    public void Dispose() => repo.Dispose();

    [Fact]
    public void PrepareWorkspace_UsesTheGivenBranch() =>
        Assert.Equal("spec/f2", TempGitRepo.Git(worktree, "branch", "--show-current"));

    [Fact]
    public async Task ImportSpec_CopiesAndStagesTheSpec()
    {
        await ImportAsync();

        Assert.Equal("# Checkout spec\n", File.ReadAllText(paths.Abs(paths.SpecRel)));
        Assert.Equal("specs/f2/spec.md", TempGitRepo.Git(worktree, "diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task ImportSpec_MissingFileIsNonRetryable()
    {
        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(() => env.RunAsync(() =>
            authoring.ImportSpecAsync(new ImportSpecInput(worktree, FeatureId, Path.Combine(repo.Root, "nope.md")))));

        Assert.True(ex.NonRetryable);
    }

    [Fact]
    public async Task PrepareStoriesDraft_RestoresSpecAndWritesPromptWithFeedback()
    {
        await ImportAsync();
        File.WriteAllText(paths.Abs(paths.SpecRel), "edited by agent");

        await env.RunAsync(() => authoring.PrepareStoriesDraftAsync(
            new PrepareStoriesDraftInput(worktree, FeatureId, Draft: 2, Feedback: "Split S1.")));

        Assert.Equal("# Checkout spec\n", File.ReadAllText(paths.Abs(paths.SpecRel)));
        var prompt = File.ReadAllText(paths.Abs(paths.StoriesPromptRel));
        Assert.Contains("This is draft 2.", prompt);
        Assert.Contains("Split S1.", prompt);
        Assert.Contains("specs/f2/stories.json", prompt);
    }

    [Fact]
    public async Task ValidateStories_AcceptsAValidDraftAndSummarizesInPriorityOrder()
    {
        await DraftStoriesAsync(ValidStories);

        var result = await ValidateAsync();

        Assert.Empty(result.Errors);
        Assert.Equal([new StorySummary("S1", "Checkout", 1), new StorySummary("S2", "Cancel", 2)], result.Stories);
        Assert.Equal(paths.Abs(paths.StoriesRel), result.StoriesPath);
    }

    [Fact]
    public async Task ValidateStories_ReportsMissingFile()
    {
        await ImportAsync();

        var result = await ValidateAsync();

        Assert.Equal(["specs/f2/stories.json was not written."], result.Errors);
    }

    [Fact]
    public async Task ValidateStories_ReportsInvalidJson()
    {
        await DraftStoriesAsync("{ not json");

        var result = await ValidateAsync();

        Assert.Contains(result.Errors, e => e.Contains("not valid JSON", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateStories_ReportsReviewProblems()
    {
        await DraftStoriesAsync(
            """
            { "featureId": "f2", "stories": [
              { "id": "S1", "title": "", "priority": 0, "acceptanceCriteria": [], "testIds": ["NotQualified"] } ] }
            """);

        var result = await ValidateAsync();

        Assert.Contains("story S1 has no title.", result.Errors);
        Assert.Contains("story S1 must have a priority of 1 or more.", result.Errors);
        Assert.Contains("story S1 must have at least one non-empty acceptance criterion.", result.Errors);
        Assert.Contains("story S1 test id 'NotQualified' is not a Namespace.Class.Method name.", result.Errors);
    }

    [Fact]
    public async Task ValidateStories_RejectsSpecEditsAndOtherFiles()
    {
        await DraftStoriesAsync(ValidStories);
        File.WriteAllText(paths.Abs(paths.SpecRel), "edited by agent");
        File.WriteAllText(Path.Combine(worktree, "Program.cs"), "// code");
        Directory.CreateDirectory(paths.Abs(paths.RalphDirRel));
        File.WriteAllText(paths.Abs(paths.ProgressRel), "notes"); // .ralph is allowed

        var result = await ValidateAsync();

        Assert.Equal(
            [
                "specs/f2/spec.md was edited; it must not change.",
                "Only specs/f2/stories.json may be written, but these also changed: Program.cs.",
            ],
            result.Errors);
    }

    [Fact]
    public async Task CommitSpec_StoriesStageCommitsOnlySpecAndStories_AndIsIdempotent()
    {
        await DraftStoriesAsync(ValidStories);
        await PreparePromptAsync();
        File.WriteAllText(Path.Combine(worktree, "stray.txt"), "not committed");
        var input = new CommitSpecInput(worktree, FeatureId, SpecCommitStage.Stories);

        var first = await env.RunAsync(() => authoring.CommitSpecAsync(input));
        var second = await env.RunAsync(() => authoring.CommitSpecAsync(input));

        Assert.Equal(first, second);
        Assert.Equal(
            "specs/f2/spec.md\nspecs/f2/stories.json",
            TempGitRepo.Git(worktree, "show", "--name-only", "--format=", "HEAD").ReplaceLineEndings("\n"));
        var message = TempGitRepo.Git(worktree, "log", "-1", "--format=%B");
        Assert.StartsWith("docs(f2): add spec and approved stories", message);
        Assert.Contains("Ralph-Spec: spec-f2/Stories", message);
    }

    [Fact]
    public async Task CommitSpec_TestsStageCommitsAcceptanceAndSolutionFiles()
    {
        await CommitStoriesAsync();
        WriteFile("specs/f2/acceptance/CheckoutTests.cs", "class CheckoutTests {}");
        WriteFile("Shop.slnx", "<Solution />");
        WriteFile("src/Shop/Leftover.cs", "// not committed");

        var result = await env.RunAsync(() =>
            authoring.CommitSpecAsync(new CommitSpecInput(worktree, FeatureId, SpecCommitStage.Tests)));

        Assert.Equal(result.CommitSha, TempGitRepo.Git(worktree, "rev-parse", "HEAD"));
        Assert.Equal(
            "Shop.slnx\nspecs/f2/acceptance/CheckoutTests.cs",
            TempGitRepo.Git(worktree, "show", "--name-only", "--format=", "HEAD").ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task PrepareTestsDraft_RestoresApprovedFilesAndKeepsEarlierTests()
    {
        await CommitStoriesAsync();
        WriteFile("specs/f2/acceptance/CheckoutTests.cs", "class CheckoutTests {}");
        File.WriteAllText(paths.Abs(paths.StoriesRel), "{}");

        await env.RunAsync(() => authoring.PrepareTestsDraftAsync(new PrepareTestsDraftInput(worktree, FeatureId, Draft: 1)));

        Assert.Equal(ValidStories, File.ReadAllText(paths.Abs(paths.StoriesRel)));
        Assert.True(File.Exists(paths.Abs("specs/f2/acceptance/CheckoutTests.cs")));
        Assert.Contains("This is draft 1.", File.ReadAllText(paths.Abs(paths.TestsPromptRel)));
    }

    [Fact]
    public async Task TestDraftViolations_AllowOnlyAcceptanceSolutionAndRalphFiles()
    {
        await CommitStoriesAsync();
        WriteFile("specs/f2/acceptance/CheckoutTests.cs", "class CheckoutTests {}");
        WriteFile("Shop.slnx", "<Solution />");
        WriteFile(".ralph/f2/last_failure.md", "notes");
        WriteFile("src/Shop/Order.cs", "// production code");
        File.WriteAllText(paths.Abs(paths.StoriesRel), "{}");

        var violations = await env.RunAsync(() => AuthoringActivities.FindTestDraftViolationsAsync(paths, CancellationToken.None));

        Assert.Equal(["specs/f2/stories.json", "src/Shop/Order.cs"], violations);
    }

    [Fact]
    public void EvaluateTests_RequiresEveryStoryTestToExistAndFail_AndEverythingElseToPass()
    {
        var results = new Dictionary<string, bool>
        {
            ["A.B.Fails"] = false,
            ["A.B.AlreadyPasses"] = true,
            ["A.Old.StillPasses"] = true,
            ["A.Old.Broken"] = false,
        };

        var evaluation = AuthoringActivities.EvaluateTests(results, ["A.B.Fails", "A.B.AlreadyPasses", "A.B.Missing"]);

        Assert.Equal(["A.B.Missing"], evaluation.Missing);
        Assert.Equal(["A.B.AlreadyPasses"], evaluation.Passing);
        Assert.Equal(["A.Old.Broken"], evaluation.Broken);
        Assert.False(evaluation.IsRed);
        Assert.True(AuthoringActivities.EvaluateTests(results, ["A.B.Fails", "A.Old.Broken"]) is { Passing.Count: 0 } e
                    && e.Missing.Count == 0 && e.Broken.Count == 0 && e.IsRed);
    }

    [Theory]
    [InlineData(AgentProfile.Implement, "PROMPT.md")]
    [InlineData(AgentProfile.DraftStories, "STORIES.prompt.md")]
    [InlineData(AgentProfile.DraftTests, "TESTS.prompt.md")]
    public void Arguments_UseThePromptAndToolsOfTheProfile(AgentProfile profile, string promptFile)
    {
        var options = new OrchestratorOptions
        {
            AllowedTools = "implement-tools",
            StoryAuthoringAllowedTools = "story-tools",
            TestAuthoringAllowedTools = "test-tools",
        };
        var expectedTools = profile switch
        {
            AgentProfile.DraftStories => "story-tools",
            AgentProfile.DraftTests => "test-tools",
            _ => "implement-tools",
        };

        var args = ClaudeCodeArguments.Build(options, "f2", profile);

        Assert.Equal($"Follow the instructions in .ralph/f2/{promptFile}", args[1]);
        Assert.Equal(expectedTools, args[args.ToList().IndexOf("--allowedTools") + 1]);
        Assert.DoesNotContain(args, a => a.Contains("dangerously", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StoriesPrompt_FirstDraftSaysThereIsNoFeedback() =>
        Assert.Contains("This is the first draft. There is no feedback yet.", PromptRenderer.RenderStories("f2", 1, null));

    private Task ImportAsync() =>
        env.RunAsync(() => authoring.ImportSpecAsync(new ImportSpecInput(worktree, FeatureId, specFile)));

    private async Task DraftStoriesAsync(string storiesJson)
    {
        await ImportAsync();
        File.WriteAllText(paths.Abs(paths.StoriesRel), storiesJson);
    }

    private Task PreparePromptAsync() =>
        env.RunAsync(() => authoring.PrepareStoriesDraftAsync(new PrepareStoriesDraftInput(worktree, FeatureId, 1, null)));

    private async Task CommitStoriesAsync()
    {
        await DraftStoriesAsync(ValidStories);
        await env.RunAsync(() => authoring.CommitSpecAsync(new CommitSpecInput(worktree, FeatureId, SpecCommitStage.Stories)));
    }

    private Task<ValidateStoriesResult> ValidateAsync() =>
        env.RunAsync(() => authoring.ValidateStoriesAsync(new ValidateStoriesInput(worktree, FeatureId)));

    private void WriteFile(string relativePath, string content)
    {
        var path = paths.Abs(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
