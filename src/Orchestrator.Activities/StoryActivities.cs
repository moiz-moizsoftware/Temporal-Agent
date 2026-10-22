using System.Text.Json;
using Orchestrator.Activities.Infrastructure;
using Orchestrator.Activities.Stories;
using Orchestrator.Contracts;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace Orchestrator.Activities;

public class StoryActivities
{
    [Activity(ActivityNames.LoadStories)]
    public Task<LoadStoriesResult> LoadStoriesAsync(LoadStoriesInput input)
    {
        var paths = new RalphPaths(input.WorktreePath, input.FeatureId);
        var storiesPath = paths.Abs(paths.StoriesRel);
        if (!File.Exists(storiesPath))
        {
            throw NonRetryable($"{paths.StoriesRel} not found.");
        }

        StoriesDocument document;
        try
        {
            document = StoriesFile.Read(storiesPath);
        }
        catch (JsonException ex)
        {
            throw NonRetryable($"{paths.StoriesRel} is not valid JSON: {ex.Message}");
        }

        var errors = StoriesFile.Validate(document, input.FeatureId);
        if (errors.Count > 0)
        {
            throw NonRetryable($"{paths.StoriesRel} is invalid: {string.Join(" ", errors)}");
        }

        var stories = document.Stories!.Select(s => new StoryRef(s.Id!, s.Priority, s.TestIds!)).ToList();
        return Task.FromResult(new LoadStoriesResult(stories));
    }

    /// <summary>
    /// Prepares one iteration: undoes any agent edits to locked spec files, writes status.json and PROMPT.md,
    /// and stages the orchestrator-owned files so Verify can detect agent edits as unstaged changes.
    /// Safe to retry: every step overwrites to the same result.
    /// </summary>
    [Activity(ActivityNames.WriteLoopFiles)]
    public async Task WriteLoopFilesAsync(WriteLoopFilesInput input)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var paths = new RalphPaths(input.WorktreePath, input.FeatureId);

        await Git.RunAsync(paths.Worktree, ct, "checkout", "HEAD", "--", paths.SpecDirRel);
        await Git.RunAsync(paths.Worktree, ct, "clean", "-fdq", "--", paths.SpecDirRel);

        var story = StoriesFile.Read(paths.Abs(paths.StoriesRel)).Stories!.Single(s => s.Id == input.StoryId);

        Directory.CreateDirectory(paths.Abs(paths.RalphDirRel));
        await File.WriteAllTextAsync(paths.Abs(paths.StatusRel), RenderStatus(input.Statuses), ct);
        await File.WriteAllTextAsync(
            paths.Abs(paths.PromptRel), PromptRenderer.Render(input.FeatureId, story, input.Iteration), ct);

        var toStage = new List<string> { "add", "--", paths.StatusRel, paths.PromptRel };
        if (File.Exists(paths.Abs(paths.LastFailureRel)))
        {
            toStage.Add(paths.LastFailureRel);
        }

        await Git.RunAsync(paths.Worktree, ct, [.. toStage]);
    }

    internal static string RenderStatus(IReadOnlyList<StoryStatus> statuses) =>
        JsonSerializer.Serialize(new { stories = statuses }, StoriesFile.Json);

    private static ApplicationFailureException NonRetryable(string message) =>
        new(message, errorType: "InvalidStories", nonRetryable: true);
}
