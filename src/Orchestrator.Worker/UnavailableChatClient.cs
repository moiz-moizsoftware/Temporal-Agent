using Microsoft.Extensions.AI;

namespace Orchestrator.Worker;

/// <summary>
/// Stands in for the summarizer when no API key is configured, so the worker still starts. Every call fails with
/// <paramref name="reason"/>, which the failure activities turn into the "please add credits" fallback.
/// </summary>
internal sealed class UnavailableChatClient(string reason) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(reason);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(reason);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
