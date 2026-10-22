using System.Diagnostics;

namespace Orchestrator.Activities.Tests;

/// <summary>A throwaway git repo with specs/f1 (spec.md + stories.json) committed. Deleted on dispose.</summary>
public sealed class TempGitRepo : IDisposable
{
    public const string FeatureId = "f1";

    public const string StoriesJson =
        """
        { "featureId": "f1",
          "stories": [
            { "id": "S1", "title": "Say hello", "priority": 1,
              "acceptanceCriteria": ["Greets by name"], "testIds": ["Hello.Tests.GreeterTests.GreetsByName"] },
            { "id": "S2", "title": "Say bye", "priority": 2,
              "acceptanceCriteria": ["Says bye"], "testIds": ["Hello.Tests.GreeterTests.SaysBye"] } ] }
        """;

    public TempGitRepo()
    {
        Root = Path.Combine(Path.GetTempPath(), "ralph-tests", Guid.NewGuid().ToString("N")[..8]);
        RepoPath = Path.Combine(Root, "repo");
        WorkRoot = Path.Combine(Root, "work");
        Directory.CreateDirectory(Path.Combine(RepoPath, "specs", FeatureId));
        File.WriteAllText(Path.Combine(RepoPath, "specs", FeatureId, "spec.md"), "# Spec\n");
        File.WriteAllText(Path.Combine(RepoPath, "specs", FeatureId, "stories.json"), StoriesJson);
        File.WriteAllText(Path.Combine(RepoPath, "README.md"), "hello\n");

        Git(RepoPath, "init", "-q", "-b", "main");
        Git(RepoPath, "config", "user.name", "Test");
        Git(RepoPath, "config", "user.email", "test@localhost");
        Git(RepoPath, "config", "core.autocrlf", "false");
        Git(RepoPath, "add", "-A");
        Git(RepoPath, "commit", "-q", "-m", "initial");
        BaseSha = Git(RepoPath, "rev-parse", "HEAD");
    }

    public string Root { get; }

    public string RepoPath { get; }

    public string WorkRoot { get; }

    public string BaseSha { get; }

    public static string Git(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0
            ? output.Trim()
            : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
    }

    public void Dispose()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        // git marks object files read-only, which blocks Directory.Delete on Windows.
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(Root, recursive: true);
    }
}
