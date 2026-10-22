using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Orchestrator.Activities.Stories;
using Orchestrator.Contracts;
using Temporalio.Testing;

namespace Orchestrator.Activities.Tests;

public sealed class StoriesAndSummaryTests : IDisposable
{
    private readonly TempGitRepo repo = new();

    public void Dispose() => repo.Dispose();

    [Fact]
    public void Validate_AcceptsWellFormedFile()
    {
        var document = System.Text.Json.JsonSerializer.Deserialize<StoriesDocument>(TempGitRepo.StoriesJson, StoriesFile.Json)!;

        Assert.Empty(StoriesFile.Validate(document, "f1"));
    }

    [Fact]
    public void Validate_ReportsEveryProblem()
    {
        var document = new StoriesDocument
        {
            FeatureId = "other",
            Stories =
            [
                new StoryDocument { Id = "S1", TestIds = ["A.B.C"] },
                new StoryDocument { Id = "S1", TestIds = ["A.B.D"] },
                new StoryDocument { Id = "S2", TestIds = [] },
            ],
        };

        var errors = StoriesFile.Validate(document, "f1");

        Assert.Contains(errors, e => e.Contains("featureId"));
        Assert.Contains(errors, e => e.Contains("duplicate story id S1"));
        Assert.Contains(errors, e => e.Contains("story S2"));
    }

    [Fact]
    public async Task SummarizeFailure_WritesLlmSummary()
    {
        var (activities, input) = Setup(new FakeChatClient(_ => "## What failed\nGreetsByName"));

        var result = await new ActivityEnvironment().RunAsync(() => activities.SummarizeFailureAsync(input));

        Assert.False(result.UsedFallback);
        var content = File.ReadAllText(result.SummaryPath);
        Assert.Contains("## What failed", content);
        Assert.Contains(input.RawLogPath, content);
    }

    [Fact]
    public async Task SummarizeFailure_FallsBackToRawTailWhenSummarizerFails()
    {
        var (activities, input) = Setup(new FakeChatClient(_ => throw new HttpRequestException("503")));

        var result = await new ActivityEnvironment().RunAsync(() => activities.SummarizeFailureAsync(input));

        Assert.True(result.UsedFallback);
        var content = File.ReadAllText(result.SummaryPath);
        Assert.Contains("error CS0103", content);
        Assert.DoesNotContain("early noise", content); // only the tail is kept
    }

    private (FailureActivities, SummarizeFailureInput) Setup(IChatClient chatClient)
    {
        var rawLog = Path.Combine(repo.Root, "verify.log");
        File.WriteAllText(rawLog, "early noise\n" + new string('.', 200) + "\nGreeter.cs(3,5): error CS0103: The name 'x' does not exist\n");
        var activities = new FailureActivities(chatClient, Options.Create(new OrchestratorOptions { RawLogTailBytes = 100 }));
        return (activities, new SummarizeFailureInput(repo.RepoPath, TempGitRepo.FeatureId, "S1", 1, rawLog));
    }

    private sealed class FakeChatClient(Func<IEnumerable<ChatMessage>, string> respond) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, respond(messages))));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
