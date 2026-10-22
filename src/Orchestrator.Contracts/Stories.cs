namespace Orchestrator.Contracts;

/// <summary>The only story data that crosses Temporal. Title and acceptance criteria stay on disk.</summary>
public record StoryRef(string Id, int Priority, IReadOnlyList<string> TestIds);

/// <summary>Per-story loop state. Mirrors one entry of <c>status.json</c>.</summary>
public record StoryStatus(string Id, bool Passes, int Attempts, string? LastFailureSignature);
