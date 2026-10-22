using System.Text.Json;

namespace Orchestrator.Activities.Stories;

/// <summary>Shape of specs/{featureId}/stories.json.</summary>
internal sealed class StoriesDocument
{
    public string? FeatureId { get; init; }

    public List<StoryDocument>? Stories { get; init; }
}

internal sealed class StoryDocument
{
    public string? Id { get; init; }

    public string? Title { get; init; }

    public int Priority { get; init; }

    public List<string>? AcceptanceCriteria { get; init; }

    public List<string>? TestIds { get; init; }
}

internal static class StoriesFile
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static StoriesDocument Read(string path) =>
        JsonSerializer.Deserialize<StoriesDocument>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"{path} is empty.");

    /// <summary>Returns every problem found; empty means valid.</summary>
    public static IReadOnlyList<string> Validate(StoriesDocument document, string expectedFeatureId)
    {
        var errors = new List<string>();
        if (document.FeatureId != expectedFeatureId)
        {
            errors.Add($"featureId '{document.FeatureId}' does not match '{expectedFeatureId}'.");
        }

        if (document.Stories is not { Count: > 0 })
        {
            errors.Add("stories is empty.");
            return errors;
        }

        foreach (var story in document.Stories)
        {
            if (string.IsNullOrWhiteSpace(story.Id))
            {
                errors.Add("a story has no id.");
                continue;
            }

            if (story.TestIds is not { Count: > 0 } || story.TestIds.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add($"story {story.Id} must have at least one non-empty testId.");
            }
        }

        errors.AddRange(document.Stories
            .Where(s => !string.IsNullOrWhiteSpace(s.Id))
            .GroupBy(s => s.Id)
            .Where(g => g.Count() > 1)
            .Select(g => $"duplicate story id {g.Key}."));
        return errors;
    }
}
