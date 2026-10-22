namespace Orchestrator.Activities.Infrastructure;

/// <summary>
/// Every path the orchestrator uses, in one place. The worktree is {WorkRoot}/{workflowId}; raw logs live
/// outside it at {WorkRoot}/logs/{workflowId} so they are never committed or seen by the locked-path check.
/// </summary>
internal sealed class RalphPaths(string worktreePath, string featureId)
{
    public string Worktree { get; } = Path.GetFullPath(worktreePath);

    public string WorkflowId => Path.GetFileName(Worktree);

    // Repo-relative paths (forward slashes, as git expects).
    public string SpecDirRel => $"specs/{featureId}";
    public string StoriesRel => $"{SpecDirRel}/stories.json";
    public string RalphDirRel => $".ralph/{featureId}";
    public string StatusRel => $"{RalphDirRel}/status.json";
    public string LastFailureRel => $"{RalphDirRel}/last_failure.md";
    public string PromptRel => $"{RalphDirRel}/PROMPT.md";
    public string ProgressRel => $"{RalphDirRel}/progress.md";

    public string Abs(string relativePath) => Path.Combine(Worktree, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public string LogsDir => Path.Combine(Path.GetDirectoryName(Worktree)!, "logs", WorkflowId);

    public string LogFile(string storyId, int iteration, string kind) =>
        Path.Combine(LogsDir, $"{storyId}-{iteration}-{kind}");
}
