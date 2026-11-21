using System.Diagnostics;
using Orchestrator.Activities.Agent;

namespace Orchestrator.Activities.Tests;

public class ClaudeCodeTests
{
    // Real output of `claude -p --max-turns 1 --output-format json` (Claude Code 2.1.0), trimmed.
    private const string SampleResult =
        """
        {"type":"result","subtype":"success","is_error":false,"duration_ms":2228,"num_turns":1,"result":"ok",
         "total_cost_usd":0.1556355,
         "usage":{"input_tokens":3,"cache_creation_input_tokens":18244,"cache_read_input_tokens":0,"output_tokens":4}}
        """;

    [Fact]
    public void Arguments_UseHeadlessJsonScopedPermissions()
    {
        var options = new OrchestratorOptions { AgentModel = "sonnet", MaxBudgetUsdPerRun = 1.5m, MaxTurns = 30 };

        var args = ClaudeCodeArguments.Build(options, "f1");

        Assert.Equal(
            [
                "-p", "Follow the instructions in .ralph/f1/PROMPT.md",
                "--output-format", "json",
                "--model", "sonnet",
                "--permission-mode", "acceptEdits",
                "--allowedTools", options.AllowedTools,
                "--max-budget-usd", "1.5",
                "--no-session-persistence",
                "--max-turns", "30",
            ],
            args);
    }

    [Fact]
    public void Arguments_OmitMaxTurnsWhenNotConfigured() =>
        Assert.DoesNotContain("--max-turns", ClaudeCodeArguments.Build(new OrchestratorOptions { MaxTurns = null }, "f1"));

    [Fact]
    public void Arguments_NeverSkipPermissions()
    {
        var args = ClaudeCodeArguments.Build(new OrchestratorOptions(), "f1");

        Assert.DoesNotContain(args, a => a.Contains("dangerously", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("bypassPermissions", args);
    }

    [Fact]
    public void Parser_ReadsMetrics()
    {
        var metrics = ClaudeResultParser.Parse(SampleResult);

        Assert.Equal(
            new ClaudeRunMetrics(Turns: 1, CostUsd: 0.1556355m, InputTokens: 18247, OutputTokens: 4, IsError: false, ResultText: "ok"),
            metrics);
    }

    [Fact]
    public void Parser_PicksCostliestModel()
    {
        var metrics = ClaudeResultParser.Parse(
            """{"modelUsage":{"claude-haiku-4-5":{"costUSD":0.01},"claude-sonnet-4-5":{"costUSD":0.9}}}""");

        Assert.Equal("claude-sonnet-4-5", metrics.Model);
    }

    [Fact]
    public void Generation_CarriesUsageCostAndContent()
    {
        using var span = new Activity("claude-code").Start();
        var metrics = new ClaudeRunMetrics(3, 0.25m, 1000, 200, IsError: false, ResultText: "done", Model: "claude-sonnet-4-5");

        ClaudeCodeRunner.RecordGeneration(span, metrics, exitCode: 0, "Follow PROMPT.md", "sonnet", captureContent: true);

        Assert.Equal("generation", span.GetTagItem("langfuse.observation.type"));
        Assert.Equal("claude-sonnet-4-5", span.GetTagItem("gen_ai.request.model"));
        Assert.Equal(1000L, span.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(200L, span.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal("""{"total":0.25}""", span.GetTagItem("langfuse.observation.cost_details"));
        Assert.Equal("done", span.GetTagItem("langfuse.observation.output"));
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
    }

    [Fact]
    public void Generation_OmitsContentWhenDisabledAndFlagsErrors()
    {
        using var span = new Activity("claude-code").Start();

        ClaudeCodeRunner.RecordGeneration(
            span, ClaudeRunMetrics.Empty with { ResultText = "secret" }, exitCode: 1, "Follow PROMPT.md", "sonnet", captureContent: false);

        Assert.Null(span.GetTagItem("langfuse.observation.input"));
        Assert.Null(span.GetTagItem("langfuse.observation.output"));
        Assert.Equal("sonnet", span.GetTagItem("gen_ai.request.model"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public void ErrorWithoutOutput_FailsTheActivityWithTheCliMessage()
    {
        // Real output when the API account has no credit.
        var metrics = ClaudeResultParser.Parse(
            """{"type":"result","subtype":"success","is_error":true,"num_turns":1,"result":"Credit balance is too low","total_cost_usd":0,"usage":{"input_tokens":0,"output_tokens":0}}""");

        var ex = Assert.Throws<Temporalio.Exceptions.ApplicationFailureException>(() => ClaudeCodeRunner.ThrowIfAgentDidNotRun(metrics));

        Assert.Contains("Credit balance is too low", ex.Message);
        Assert.Equal("AgentUnavailable", ex.ErrorType);
    }

    [Fact]
    public void ErrorAfterDoingWork_IsLeftToVerify() =>
        ClaudeCodeRunner.ThrowIfAgentDidNotRun(new ClaudeRunMetrics(30, 1.2m, 5000, 800, IsError: true, "max turns"));

    [Fact]
    public void Cli_NeverSeesTheSummarizerApiKey() =>
        Assert.Contains("ANTHROPIC_API_KEY", ClaudeCodeRunner.HiddenFromCli);

    [Fact]
    public async Task ProcessRunner_RemovesHiddenVariablesFromChildEnvironment()
    {
        var name = $"RALPH_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, "leaked");
        var dir = Directory.CreateTempSubdirectory("ralph-env").FullName;
        try
        {
            var (exe, args) = OperatingSystem.IsWindows()
                ? ("cmd", new[] { "/c", $"echo [%{name}%]" })
                : ("sh", new[] { "-c", $"echo [${name}]" });

            await Orchestrator.Activities.Infrastructure.ProcessRunner.RunToFileAsync(
                exe, args, dir, Path.Combine(dir, "inherited.txt"), null, CancellationToken.None);
            await Orchestrator.Activities.Infrastructure.ProcessRunner.RunToFileAsync(
                exe, args, dir, Path.Combine(dir, "hidden.txt"), null, CancellationToken.None, [name]);

            Assert.Contains("leaked", File.ReadAllText(Path.Combine(dir, "inherited.txt"))); // the test can see a leak
            Assert.DoesNotContain("leaked", File.ReadAllText(Path.Combine(dir, "hidden.txt")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Parser_MissingFieldsAreNull() =>
        Assert.Equal(new ClaudeRunMetrics(2, null, null, null, null), ClaudeResultParser.Parse("""{"num_turns":2}"""));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void Parser_GarbageGivesEmpty(string output) =>
        Assert.Equal(ClaudeRunMetrics.Empty, ClaudeResultParser.Parse(output));
}
