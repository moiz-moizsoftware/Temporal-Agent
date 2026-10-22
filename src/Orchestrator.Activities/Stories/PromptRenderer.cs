namespace Orchestrator.Activities.Stories;

/// <summary>Renders PROMPT.md from the embedded template. Plain {{placeholder}} replacement.</summary>
internal static class PromptRenderer
{
    private static readonly Lazy<string> Template = new(LoadTemplate);

    public static string Render(string featureId, StoryDocument story, int iteration) =>
        Template.Value
            .Replace("{{featureId}}", featureId)
            .Replace("{{storyId}}", story.Id)
            .Replace("{{title}}", story.Title ?? "")
            .Replace("{{iteration}}", iteration.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{{acceptanceCriteria}}", Bullets(story.AcceptanceCriteria))
            .Replace("{{testIds}}", Bullets(story.TestIds?.Select(t => $"`{t}`")));

    private static string Bullets(IEnumerable<string>? items) =>
        items is null ? "- (none)" : string.Join('\n', items.Select(i => $"- {i}"));

    private static string LoadTemplate()
    {
        using var stream = typeof(PromptRenderer).Assembly.GetManifestResourceStream("PROMPT.template.md")
            ?? throw new InvalidOperationException("PROMPT.template.md resource not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
