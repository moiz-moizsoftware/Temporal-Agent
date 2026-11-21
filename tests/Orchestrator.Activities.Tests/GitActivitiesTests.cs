using Microsoft.Extensions.Options;
using Orchestrator.Activities.Infrastructure;
using Orchestrator.Activities.Verification;
using Orchestrator.Contracts;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace Orchestrator.Activities.Tests;

public sealed class GitActivitiesTests : IDisposable
{
    private readonly TempGitRepo repo = new();
    private readonly ActivityEnvironment env = new();
    private readonly WorkspaceActivities workspace;
    private readonly StoryActivities stories = new();

    public GitActivitiesTests() =>
        workspace = new WorkspaceActivities(Options.Create(new OrchestratorOptions { WorkRoot = repo.WorkRoot }));

    public void Dispose() => repo.Dispose();

    [Fact]
    public async Task PrepareWorkspace_IsIdempotent()
    {
        var input = new PrepareWorkspaceInput(repo.RepoPath, repo.BaseSha, TempGitRepo.FeatureId, "feature-f1");

        var first = await env.RunAsync(() => workspace.PrepareWorkspaceAsync(input));
        var second = await env.RunAsync(() => workspace.PrepareWorkspaceAsync(input));

        Assert.Equal(first, second);
        Assert.Equal("ralph/f1", first.BranchName);
        Assert.Equal(repo.BaseSha, TempGitRepo.Git(first.WorktreePath, "rev-parse", "HEAD"));
        Assert.Equal("ralph/f1", TempGitRepo.Git(first.WorktreePath, "branch", "--show-current"));
    }

    [Fact]
    public async Task CommitStory_RetryDoesNotCreateSecondCommit()
    {
        var worktree = await PrepareAsync();
        File.WriteAllText(Path.Combine(worktree, "Greeter.cs"), "class Greeter {}");
        var input = new CommitStoryInput(worktree, TempGitRepo.FeatureId, "S1", Iteration: 1);

        var first = await env.RunAsync(() => workspace.CommitStoryAsync(input));
        var second = await env.RunAsync(() => workspace.CommitStoryAsync(input));

        Assert.Equal(first.CommitSha, second.CommitSha);
        Assert.Equal("1", TempGitRepo.Git(worktree, "rev-list", "--count", $"{repo.BaseSha}..HEAD"));
        var message = TempGitRepo.Git(worktree, "log", "-1", "--format=%B");
        Assert.StartsWith("feat(f1): S1 Say hello", message);
        Assert.Contains("Ralph-Story: feature-f1/S1", message);
    }

    [Fact]
    public async Task CommitStory_RemovesStrayNulFileThatGitCannotIndex()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // "nul" is only special on Windows
        }

        var worktree = await PrepareAsync();
        File.WriteAllText(Path.Combine(worktree, "Greeter.cs"), "class Greeter {}");
        var nul = @"\\?\" + Path.Combine(worktree, "nul"); // what `cmd 2>nul` leaves behind in Git Bash
        File.WriteAllText(nul, "noise");

        await env.RunAsync(() => workspace.CommitStoryAsync(new CommitStoryInput(worktree, TempGitRepo.FeatureId, "S1", 1)));

        Assert.False(File.Exists(nul));
        Assert.Equal("Greeter.cs", TempGitRepo.Git(worktree, "show", "--name-only", "--format=", "HEAD"));
    }

    [Theory]
    [InlineData("nul", true)]
    [InlineData("src/NUL.txt", true)]
    [InlineData("com1", true)]
    [InlineData("null.cs", false)]
    [InlineData("console.txt", false)]
    public void ReservedNames_AreRecognised(string path, bool reserved) =>
        Assert.Equal(reserved, ReservedNameFiles.IsReserved(path));

    [Fact]
    public async Task LoadStories_ReturnsStoryRefs()
    {
        var worktree = await PrepareAsync();

        var result = await env.RunAsync(() => stories.LoadStoriesAsync(new LoadStoriesInput(worktree, TempGitRepo.FeatureId)));

        Assert.Equal(["S1", "S2"], result.Stories.Select(s => s.Id));
        Assert.Equal(["Hello.Tests.GreeterTests.GreetsByName"], result.Stories[0].TestIds);
    }

    [Fact]
    public async Task LoadStories_InvalidFile_FailsNonRetryable()
    {
        var worktree = await PrepareAsync();
        File.WriteAllText(Path.Combine(worktree, "specs", "f1", "stories.json"), """{ "featureId": "f1", "stories": [ { "id": "S1", "testIds": [] } ] }""");

        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => env.RunAsync(() => stories.LoadStoriesAsync(new LoadStoriesInput(worktree, TempGitRepo.FeatureId))));

        Assert.True(ex.NonRetryable);
        Assert.Contains("S1", ex.Message);
    }

    [Fact]
    public async Task WriteLoopFiles_RestoresLockedFilesAndStagesOwnedFiles()
    {
        var worktree = await PrepareAsync();
        var specPath = Path.Combine(worktree, "specs", "f1", "spec.md");
        File.WriteAllText(specPath, "tampered");
        File.WriteAllText(Path.Combine(worktree, "specs", "f1", "extra.cs"), "new file");
        var statuses = new List<StoryStatus> { new("S1", false, 1, "failed:X"), new("S2", false, 0, null) };

        await env.RunAsync(() => stories.WriteLoopFilesAsync(
            new WriteLoopFilesInput(worktree, TempGitRepo.FeatureId, "S1", Iteration: 2, statuses)));

        Assert.Equal("# Spec\n", File.ReadAllText(specPath));
        Assert.False(File.Exists(Path.Combine(worktree, "specs", "f1", "extra.cs")));
        var prompt = File.ReadAllText(Path.Combine(worktree, ".ralph", "f1", "PROMPT.md"));
        Assert.Contains("Story S1: Say hello", prompt);
        Assert.Contains("`Hello.Tests.GreeterTests.GreetsByName`", prompt);
        Assert.Contains("\"lastFailureSignature\": \"failed:X\"", File.ReadAllText(Path.Combine(worktree, ".ralph", "f1", "status.json")));
        Assert.Contains(".ralph/f1/status.json", TempGitRepo.Git(worktree, "diff", "--cached", "--name-only"));
        Assert.Empty(await LockedViolationsAsync(worktree));
    }

    [Fact]
    public async Task LockedPathCheck_FlagsSpecAndStatusEdits()
    {
        var worktree = await PrepareAsync();
        await env.RunAsync(() => stories.WriteLoopFilesAsync(new WriteLoopFilesInput(
            worktree, TempGitRepo.FeatureId, "S1", 1, [new StoryStatus("S1", false, 0, null)])));

        File.AppendAllText(Path.Combine(worktree, "specs", "f1", "spec.md"), "agent edit");
        File.WriteAllText(Path.Combine(worktree, ".ralph", "f1", "status.json"), "{}");
        File.WriteAllText(Path.Combine(worktree, "Greeter.cs"), "allowed");

        Assert.Equal([".ralph/f1/status.json", "specs/f1/spec.md"], await LockedViolationsAsync(worktree));
    }

    [Fact]
    public async Task Packages_DetectsAddedAndBumpedSinceHead()
    {
        var worktree = await PrepareAsync();
        var csproj = Path.Combine(worktree, "App.csproj");
        File.WriteAllText(csproj, """<Project><ItemGroup><PackageReference Include="Polly" Version="8.0.0" /></ItemGroup></Project>""");
        TempGitRepo.Git(worktree, "add", "-A");
        TempGitRepo.Git(worktree, "-c", "user.name=T", "-c", "user.email=t@localhost", "commit", "-q", "-m", "base");

        File.WriteAllText(csproj, """<Project><ItemGroup><PackageReference Include="Polly" Version="8.5.0" /><PackageReference Include="Dapper" Version="2.1.0" /></ItemGroup></Project>""");
        Directory.CreateDirectory(Path.Combine(worktree, "src", "New"));
        File.WriteAllText(Path.Combine(worktree, "src", "New", "New.csproj"), """<Project><ItemGroup><PackageReference Include="Serilog" Version="4.0.0" /></ItemGroup></Project>""");

        Assert.Equal(
            ["Dapper@2.1.0", "Polly@8.5.0", "Serilog@4.0.0"],
            await PackageChanges.FindAddedAsync(worktree, CancellationToken.None));
    }

    private Task<IReadOnlyList<string>> LockedViolationsAsync(string worktree) =>
        VerifyActivities.FindLockedPathViolationsAsync(new RalphPaths(worktree, TempGitRepo.FeatureId), CancellationToken.None);

    private async Task<string> PrepareAsync() =>
        (await env.RunAsync(() => workspace.PrepareWorkspaceAsync(
            new PrepareWorkspaceInput(repo.RepoPath, repo.BaseSha, TempGitRepo.FeatureId, "feature-f1")))).WorktreePath;
}
