using System.Text.Json;
using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Orchestrator.Activities;
using Orchestrator.Activities.Agent;
using Orchestrator.Activities.Telemetry;
using Orchestrator.Contracts;
using Orchestrator.Worker;
using Orchestrator.Workflows;
using Temporalio.Converters;
using Temporalio.Extensions.Hosting;
using Temporalio.Extensions.OpenTelemetry;

var builder = Host.CreateApplicationBuilder(args);

var section = builder.Configuration.GetSection(OrchestratorOptions.SectionName);
var settings = section.Get<OrchestratorOptions>() ?? new OrchestratorOptions();

builder.Services.AddOptions<OrchestratorOptions>()
    .Bind(section)
    .Validate(o => !string.IsNullOrWhiteSpace(o.WorkRoot), "Orchestrator:WorkRoot is required.")
    .ValidateOnStart();

// LANGFUSE_SECRET_KEY is read from the environment only, like ANTHROPIC_API_KEY. No keys = tracing off.
var langfuseSection = builder.Configuration.GetSection(LangfuseOptions.SectionName);
var langfuse = langfuseSection.Get<LangfuseOptions>() ?? new LangfuseOptions();
langfuse.SecretKey = Environment.GetEnvironmentVariable(LangfuseOptions.SecretKeyVariable) ?? "";
builder.Services.AddOptions<LangfuseOptions>()
    .Bind(langfuseSection)
    .Configure(o => o.SecretKey = langfuse.SecretKey);

EnsureOnPath("git");
EnsureOnPath("dotnet");
EnsureOnPath(settings.ClaudeCliPath);

// ANTHROPIC_API_KEY is read from the environment by the Anthropic client; it is never in config files.
builder.Services.AddSingleton<IChatClient>(sp =>
    new AnthropicClient()
        .AsIChatClient(sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value.SummarizerModel)
        .AsBuilder()
        .UseOpenTelemetry(sourceName: OrchestratorTelemetry.SummarizerSourceName, configure: c => c.EnableSensitiveData = langfuse.CaptureContent)
        .Build(sp));
builder.Services.AddSingleton<IAgentRunner, ClaudeCodeRunner>();

if (langfuse.Enabled)
{
    AddLangfuseTracing(builder.Services, langfuse);
    builder.Services.AddHttpClient<ILangfuseScores, LangfuseScoresClient>(c => c.Timeout = TimeSpan.FromSeconds(10));
}
else
{
    builder.Services.AddSingleton<ILangfuseScores>(NullLangfuseScores.Instance);
}

builder.Services
    .AddHostedTemporalWorker(settings.TemporalAddress, settings.Namespace, TaskQueues.Orchestrator, deploymentOptions: null)
    .ConfigureOptions(o =>
    {
        // camelCase JSON from the CLI (workflow input, signals) binds; the SDK default is case-sensitive PascalCase.
        o.ClientOptions!.DataConverter = DataConverter.Default with
        {
            PayloadConverter = new DefaultPayloadConverter(new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        // One trace per workflow run: activity spans are children of the workflow span. Harmless when not exported.
        o.ClientOptions.Interceptors = [new TracingInterceptor()];
    })
    .AddWorkflow<FeatureWorkflow>()
    .AddScopedActivities<WorkspaceActivities>()
    .AddScopedActivities<StoryActivities>()
    .AddScopedActivities<AgentActivities>()
    .AddScopedActivities<VerifyActivities>()
    .AddScopedActivities<FailureActivities>()
    .AddScopedActivities<TelemetryActivities>();

builder.Build().Run();

// Langfuse ingests OTLP over HTTP/protobuf (not gRPC) with Basic auth. Disposing the host flushes pending spans.
static void AddLangfuseTracing(IServiceCollection services, LangfuseOptions langfuse) =>
    services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("orchestrator-worker"))
        .WithTracing(t => t
            .AddSource(
                TracingInterceptor.ClientSource.Name,
                TracingInterceptor.WorkflowsSource.Name,
                TracingInterceptor.ActivitiesSource.Name,
                OrchestratorTelemetry.SourceName,
                OrchestratorTelemetry.SummarizerSourceName)
            .AddProcessor(new LangfuseEnrichmentProcessor(langfuse.Environment, new TracingInterceptorOptions().TagNameWorkflowId))
            .AddOtlpExporter(o =>
            {
                o.Endpoint = new Uri($"{langfuse.BaseUrl.TrimEnd('/')}/api/public/otel/v1/traces");
                o.Protocol = OtlpExportProtocol.HttpProtobuf;
                o.Headers = $"Authorization=Basic {langfuse.BasicAuth}";
            }));

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
