namespace Orchestrator.Activities;

/// <summary>Worker configuration, bound from the "Orchestrator" section and ORCHESTRATOR__* env vars. No secrets.</summary>
public sealed class OrchestratorOptions
{
    public const string SectionName = "Orchestrator";

    /// <summary>Root folder for worktrees ({WorkRoot}/{workflowId}) and raw logs ({WorkRoot}/logs/{workflowId}).</summary>
    public string WorkRoot { get; set; } = "";

    public string TemporalAddress { get; set; } = "localhost:7233";

    public string Namespace { get; set; } = "default";

    public string ClaudeCliPath { get; set; } = OperatingSystem.IsWindows() ? "claude.cmd" : "claude";

    public string AgentModel { get; set; } = "sonnet";

    public decimal MaxBudgetUsdPerRun { get; set; } = 2.0m;

    public int? MaxTurns { get; set; } = 50;

    /// <summary>
    /// Comma-separated, passed as a single --allowedTools value. Added packages are gated by human approval in
    /// the workflow, not here.
    /// </summary>
    public string AllowedTools { get; set; } =
        "Read,Edit,Write,Glob,Grep,Bash(dotnet build:*),Bash(dotnet test:*),Bash(dotnet add package:*)";

    /// <summary>Tools for drafting stories.json: read the spec, write one file. No shell.</summary>
    public string StoryAuthoringAllowedTools { get; set; } = "Read,Write,Glob,Grep";

    /// <summary>
    /// Tools for drafting acceptance tests: create a test project, add it to the solution, add test packages, build and
    /// run it. VerifyTests rejects any change outside the acceptance folder and solution files.
    /// </summary>
    public string TestAuthoringAllowedTools { get; set; } =
        "Read,Edit,Write,Glob,Grep,Bash(dotnet build:*),Bash(dotnet test:*),Bash(dotnet new:*),Bash(dotnet sln:*),Bash(dotnet add:*)";

    public string SummarizerModel { get; set; } = "claude-haiku-5-5";

    public int RawLogTailBytes { get; set; } = 16384;
}
