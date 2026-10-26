using System.Xml;
using System.Xml.Linq;
using Orchestrator.Activities.Infrastructure;

namespace Orchestrator.Activities.Verification;

/// <summary>
/// NuGet packages the agent added or re-versioned since HEAD, as "Name@Version". Covers PackageReference and
/// PackageVersion (central package management) in any changed or new *.csproj, *.props or *.targets file,
/// whether written by `dotnet add package` or by hand.
/// </summary>
internal static class PackageChanges
{
    public static async Task<IReadOnlyList<string>> FindAddedAsync(string worktree, CancellationToken ct)
    {
        // Not Git.RunAsync: trimming would eat the leading status column of the first line.
        var status = await Git.ExecAsync(worktree, ct, "status", "--porcelain", "--untracked-files=all");
        var files = status.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Length > 3 && line[1] != 'D' && line[0] != 'D')
            .Select(line => line[3..].Split(" -> ")[^1].Trim().Trim('"'))
            .Where(IsMsBuildFile);

        var added = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var before = await Git.ExecAsync(worktree, ct, "show", $"HEAD:{file}");
            var after = await File.ReadAllTextAsync(Path.Combine(worktree, file), ct);
            added.UnionWith(Parse(after).Except(before.ExitCode == 0 ? Parse(before.StdOut) : [], StringComparer.OrdinalIgnoreCase));
        }

        return added.ToList();
    }

    /// <summary>Packages declared in an MSBuild file; empty when the XML is invalid (the build reports that).</summary>
    internal static IReadOnlyList<string> Parse(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return [];
        }

        return document.Descendants()
            .Where(e => e.Name.LocalName is "PackageReference" or "PackageVersion")
            .Select(e => (Name: (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update"),
                          Version: (string?)e.Attribute("Version") ?? e.Elements().FirstOrDefault(c => c.Name.LocalName == "Version")?.Value))
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => string.IsNullOrWhiteSpace(p.Version) ? p.Name! : $"{p.Name}@{p.Version}")
            .ToList();
    }

    private static bool IsMsBuildFile(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase);
}
