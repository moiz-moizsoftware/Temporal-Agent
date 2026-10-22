using System.Xml.Linq;

namespace Orchestrator.Activities.Verification;

/// <summary>Reads VSTest .trx files into "Namespace.Class.Method" → passed.</summary>
public static class TrxParser
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    /// <summary>Parses every .trx in the folder (one per test project). Missing folder means no results.</summary>
    public static IReadOnlyDictionary<string, bool> ParseDirectory(string directory) =>
        Directory.Exists(directory)
            ? Merge(Directory.EnumerateFiles(directory, "*.trx", SearchOption.AllDirectories).Select(f => Parse(XDocument.Load(f))))
            : new Dictionary<string, bool>();

    /// <summary>
    /// A method passes only if every result for it passed (theories produce several results per method).
    /// </summary>
    public static IReadOnlyDictionary<string, bool> Parse(XDocument trx)
    {
        var methodById = trx.Descendants(Ns + "UnitTest")
            .Select(t => (Id: (string?)t.Attribute("id"), Method: t.Element(Ns + "TestMethod")))
            .Where(t => t.Id is not null && t.Method is not null)
            .ToDictionary(
                t => t.Id!,
                t => $"{StripAssembly((string?)t.Method!.Attribute("className"))}.{(string?)t.Method!.Attribute("name")}");

        return trx.Descendants(Ns + "UnitTestResult")
            .Select(r => (Id: (string?)r.Attribute("testId"), Outcome: (string?)r.Attribute("outcome")))
            .Where(r => r.Id is not null && methodById.ContainsKey(r.Id))
            .GroupBy(r => methodById[r.Id!])
            .ToDictionary(g => g.Key, g => g.All(r => r.Outcome == "Passed"));
    }

    private static IReadOnlyDictionary<string, bool> Merge(IEnumerable<IReadOnlyDictionary<string, bool>> results)
    {
        var merged = new Dictionary<string, bool>();
        foreach (var (name, passed) in results.SelectMany(r => r))
        {
            merged[name] = merged.TryGetValue(name, out var existing) ? existing && passed : passed;
        }

        return merged;
    }

    // Some adapters write "Namespace.Class, AssemblyName".
    private static string StripAssembly(string? className) => className?.Split(',')[0].Trim() ?? "";
}
