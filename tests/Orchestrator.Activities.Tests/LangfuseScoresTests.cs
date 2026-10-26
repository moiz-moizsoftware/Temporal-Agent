using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orchestrator.Activities.Telemetry;

namespace Orchestrator.Activities.Tests;

public class LangfuseScoresTests
{
    private static readonly LangfuseOptions Options = new()
    {
        BaseUrl = "https://langfuse.test/", PublicKey = "pk", SecretKey = "sk", Environment = "test",
    };

    [Fact]
    public async Task Post_SendsScoreForCurrentTraceWithStableId()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var span = new Activity("RunActivity:Verify").Start();

        await Client(handler).PostAsync("verify_passed", 1, ScoreDataType.Boolean, "S1-2", onSpan: true, CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal("https://langfuse.test/api/public/scores", request.Uri);
        Assert.Equal("Basic " + Options.BasicAuth, request.Authorization);
        var body = JsonDocument.Parse(request.Body).RootElement;
        var traceId = span.TraceId.ToHexString();
        Assert.Equal($"{traceId}-verify_passed-S1-2", body.GetProperty("id").GetString());
        Assert.Equal(traceId, body.GetProperty("traceId").GetString());
        Assert.Equal(span.SpanId.ToHexString(), body.GetProperty("observationId").GetString());
        Assert.Equal("BOOLEAN", body.GetProperty("dataType").GetString());
        Assert.Equal(1, body.GetProperty("value").GetInt32());
        Assert.Equal("test", body.GetProperty("environment").GetString());
    }

    [Fact]
    public async Task Post_TraceLevelScoreOmitsObservationId()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var span = new Activity("RunActivity:RecordFeatureOutcome").Start();

        await Client(handler).PostAsync("feature_outcome", "Completed", ScoreDataType.Categorical, "f1", onSpan: false, CancellationToken.None);

        var body = JsonDocument.Parse(handler.Requests.Single().Body).RootElement;
        Assert.False(body.TryGetProperty("observationId", out _));
        Assert.Equal("Completed", body.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Post_WithoutSpanSendsNothing()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        Activity.Current = null;

        await Client(handler).PostAsync("verify_passed", 0, ScoreDataType.Boolean, "S1-1", onSpan: true, CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Post_ServerErrorIsSwallowed()
    {
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError);
        using var span = new Activity("RunActivity:Verify").Start();

        await Client(handler).PostAsync("verify_passed", 0, ScoreDataType.Boolean, "S1-1", onSpan: true, CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    private static LangfuseScoresClient Client(RecordingHandler handler) =>
        new(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(Options), NullLogger<LangfuseScoresClient>.Instance);

    private sealed record SentRequest(string Uri, string? Authorization, string Body);

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new SentRequest(
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status);
        }
    }
}
