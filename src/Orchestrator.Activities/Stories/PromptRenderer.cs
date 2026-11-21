using System.Collections.Concurrent;
using System.Globalization;

namespace Orchestrator.Activities.Stories;

/// <summary>Renders the agent prompts from embedded templates. Plain {{placeholder}} replacement.</summary>
internal static class PromptRenderer
{
    private static readonly ConcurrentDictionary<string, string> Templates = new();

    /// <summary>PROMPT.md: implement one story.</summary>
    public static string Render(string featureId, StoryDocument story, int iteration) =>
        Template("PROMPT.template.md")
            .Replace("{{featureId}}", featureId)
            .Replace("{{storyId}}", story.Id)
            .Replace("{{title}}", story.Title ?? "")
            .Replace("{{iteration}}", iteration.ToString(CultureInfo.InvariantCulture))
            .Replace("{{acceptanceCriteria}}", Bullets(story.AcceptanceCriteria))
            .Replace("{{testIds}}", Bullets(story.TestIds?.Select(t => $"`{t}`")));

    /// <summary>STORIES.prompt.md: draft stories.json from the spec, revising the previous draft per feedback.</summary>
    public static string RenderStories(string featureId, int draft, string? feedback) =>
        Template("STORIES.template.md")
            .Replace("{{featureId}}", featureId)
            .Replace("{{draft}}", draft.ToString(CultureInfo.InvariantCulture))
            .Replace("{{feedback}}", string.IsNullOrWhiteSpace(feedback)
                ? "This is the first draft. There is no feedback yet."
                : feedback.Trim());

    /// <summary>TESTS.prompt.md: write failing acceptance tests for every approved story.</summary>
    public static string RenderTests(string featureId, int draft) =>
        Template("TESTS.template.md")
            .Replace("{{featureId}}", featureId)
            .Replace("{{draft}}", draft.ToString(CultureInfo.InvariantCulture));

    private static string Bullets(IEnumerable<string>? items) =>
        items is null ? "- (none)" : string.Join('\n', items.Select(i => $"- {i}"));

    private static string Template(string name) => Templates.GetOrAdd(name, Load);

    private static string Load(string name)
    {
        using var stream = typeof(PromptRenderer).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} resource not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
