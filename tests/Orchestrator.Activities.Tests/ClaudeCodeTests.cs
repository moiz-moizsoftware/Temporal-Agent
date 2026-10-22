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

        Assert.Equal(new ClaudeRunMetrics(Turns: 1, CostUsd: 0.1556355m, InputTokens: 18247, OutputTokens: 4, IsError: false), metrics);
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
