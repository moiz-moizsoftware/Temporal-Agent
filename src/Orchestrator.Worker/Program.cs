using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Orchestrator.Activities;
using Orchestrator.Activities.Agent;
using Orchestrator.Contracts;
using Orchestrator.Workflows;
using Temporalio.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

var section = builder.Configuration.GetSection(OrchestratorOptions.SectionName);
var settings = section.Get<OrchestratorOptions>() ?? new OrchestratorOptions();

builder.Services.AddOptions<OrchestratorOptions>()
    .Bind(section)
    .Validate(o => !string.IsNullOrWhiteSpace(o.WorkRoot), "Orchestrator:WorkRoot is required.")
    .ValidateOnStart();

EnsureOnPath("git");
EnsureOnPath("dotnet");
EnsureOnPath(settings.ClaudeCliPath);

// ANTHROPIC_API_KEY is read from the environment by the Anthropic client; it is never in config files.
builder.Services.AddSingleton<IChatClient>(sp =>
    new AnthropicClient().AsIChatClient(sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value.SummarizerModel));
builder.Services.AddSingleton<IAgentRunner, ClaudeCodeRunner>();

builder.Services
    .AddHostedTemporalWorker(settings.TemporalAddress, settings.Namespace, TaskQueues.Orchestrator, deploymentOptions: null)
    .AddWorkflow<FeatureWorkflow>()
    .AddScopedActivities<WorkspaceActivities>()
    .AddScopedActivities<StoryActivities>()
    .AddScopedActivities<AgentActivities>()
    .AddScopedActivities<VerifyActivities>()
    .AddScopedActivities<FailureActivities>();

builder.Build().Run();

// Fail fast at startup rather than on the first activity.
static void EnsureOnPath(string executable)
{
    if (Path.IsPathRooted(executable) ? File.Exists(executable) : FindOnPath(executable))
    {
        return;
    }

    throw new InvalidOperationException($"'{executable}' was not found. Install it or fix the configured path.");
}

static bool FindOnPath(string name)
{
    var extensions = OperatingSystem.IsWindows() && !Path.HasExtension(name)
        ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD").Split(';')
        : [""];
    return (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(dir => extensions.Any(ext => File.Exists(Path.Combine(dir, name + ext))));
}
