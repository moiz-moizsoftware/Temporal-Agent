using Microsoft.Extensions.Options;
using Orchestrator.Activities.Infrastructure;
using Orchestrator.Activities.Stories;
using Orchestrator.Contracts;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace Orchestrator.Activities;

public class WorkspaceActivities(IOptions<OrchestratorOptions> options)
{
    private const string StoryTrailer = "Ralph-Story";

    /// <summary>
    /// Creates (or reuses) the worktree {WorkRoot}/{workflowId} on branch ralph/{featureId}.
    /// Idempotent: a retry finds the existing worktree or branch and never creates a duplicate.
    /// </summary>
    [Activity(ActivityNames.PrepareWorkspace)]
    public async Task<PrepareWorkspaceResult> PrepareWorkspaceAsync(PrepareWorkspaceInput input)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var branch = $"ralph/{input.FeatureId}";
        var worktree = Path.GetFullPath(Path.Combine(options.Value.WorkRoot, input.WorkflowId));

        await Git.RunAsync(input.RepoPath, ct, "worktree", "prune");
        if (await IsRegisteredWorktreeAsync(input.RepoPath, worktree, ct))
        {
            return new PrepareWorkspaceResult(worktree, branch);
        }

        if (Directory.Exists(worktree) && Directory.EnumerateFileSystemEntries(worktree).Any())
        {
            throw new ApplicationFailureException(
                $"{worktree} exists but is not a worktree of {input.RepoPath}.", errorType: "WorkspaceConflict", nonRetryable: true);
        }

        var branchExists = (await Git.ExecAsync(
            input.RepoPath, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}")).ExitCode == 0;

        if (branchExists)
        {
            await Git.RunAsync(input.RepoPath, ct, "worktree", "add", worktree, branch);
        }
        else
        {
            await Git.RunAsync(input.RepoPath, ct, "worktree", "add", "-b", branch, worktree, input.BaseCommitSha);
        }

        return new PrepareWorkspaceResult(worktree, branch);
    }

    /// <summary>
    /// Commits everything in the worktree for a passing story. Idempotent: the commit carries a
    /// "Ralph-Story: {workflowId}/{storyId}" trailer, and a retry returns the existing commit instead.
    /// </summary>
    [Activity(ActivityNames.CommitStory)]
    public async Task<CommitStoryResult> CommitStoryAsync(CommitStoryInput input)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var paths = new RalphPaths(input.WorktreePath, input.FeatureId);
        var trailerValue = $"{paths.WorkflowId}/{input.StoryId}";

        if (await FindStoryCommitAsync(paths.Worktree, trailerValue, ct) is { } existing)
        {
            return new CommitStoryResult(existing);
        }

        var title = StoriesFile.Read(paths.Abs(paths.StoriesRel)).Stories?.FirstOrDefault(s => s.Id == input.StoryId)?.Title;
        var subject = $"feat({input.FeatureId}): {input.StoryId} {title}".TrimEnd();
        var trailers = $"{StoryTrailer}: {trailerValue}\nRalph-Iteration: {input.Iteration}";

        await Git.RunAsync(paths.Worktree, ct, "add", "-A");
        await Git.RunAsync(
            paths.Worktree, ct,
            "-c", "user.name=Ralph Orchestrator", "-c", "user.email=ralph-orchestrator@localhost",
            "commit", "--allow-empty", "-m", subject, "-m", trailers);
        return new CommitStoryResult(await Git.RunAsync(paths.Worktree, ct, "rev-parse", "HEAD"));
    }

    private static async Task<bool> IsRegisteredWorktreeAsync(string repoPath, string worktree, CancellationToken ct)
    {
        var list = await Git.RunAsync(repoPath, ct, "worktree", "list", "--porcelain");
        return list.Split('\n')
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(line => Path.GetFullPath(line["worktree ".Length..].Trim()))
            .Any(path => string.Equals(
                path, worktree, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }

    private static async Task<string?> FindStoryCommitAsync(string worktree, string trailerValue, CancellationToken ct)
    {
        // %x1f separates sha from message, %x1e separates commits.
        var log = await Git.RunAsync(worktree, ct, "log", "-n", "200", "--format=%H%x1f%B%x1e");
        var expected = $"{StoryTrailer}: {trailerValue}";
        return log.Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Trim().Split('\x1f', 2))
            .Where(parts => parts.Length == 2 && parts[1].Split('\n').Any(line => line.Trim() == expected))
            .Select(parts => parts[0])
            .FirstOrDefault();
    }
}
