namespace Orchestrator.Contracts;

/// <summary>Activity names shared by the workflow (caller) and the worker (implementation).</summary>
public static class ActivityNames
{
    public const string PrepareWorkspace = nameof(PrepareWorkspace);
    public const string LoadStories = nameof(LoadStories);
    public const string WriteLoopFiles = nameof(WriteLoopFiles);
    public const string RunAgent = nameof(RunAgent);
    public const string Verify = nameof(Verify);
    public const string CommitStory = nameof(CommitStory);
    public const string SummarizeFailure = nameof(SummarizeFailure);
}

public static class TaskQueues
{
    public const string Orchestrator = "orchestrator";
}
