namespace Orchestrator.Activities.Infrastructure;

/// <summary>Thin git helpers. All commands run with the given working directory.</summary>
internal static class Git
{
    public static Task<ProcessRunner.Captured> ExecAsync(string workingDirectory, CancellationToken ct, params string[] args) =>
        ProcessRunner.RunCapturedAsync("git", args, workingDirectory, ct);

    /// <summary>Runs git and returns trimmed stdout; throws when git exits non-zero.</summary>
    public static async Task<string> RunAsync(string workingDirectory, CancellationToken ct, params string[] args)
    {
        var result = await ExecAsync(workingDirectory, ct, args);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.StdErr.Trim()}");
        }

        return result.StdOut.Trim();
    }

    /// <summary>Commits what is staged as the orchestrator, with a subject and a trailer block. Returns the new SHA.</summary>
    public static async Task<string> CommitAsOrchestratorAsync(
        string worktree, CancellationToken ct, string subject, string trailers)
    {
        await RunAsync(
            worktree, ct,
            "-c", "user.name=Ralph Orchestrator", "-c", "user.email=ralph-orchestrator@localhost",
            "commit", "--allow-empty", "-m", subject, "-m", trailers);
        return await RunAsync(worktree, ct, "rev-parse", "HEAD");
    }

    /// <summary>
    /// The most recent of the last 200 commits whose message has the exact line "{trailer}", or null.
    /// Lets commit activities return the existing commit on retry instead of committing twice.
    /// </summary>
    public static async Task<string?> FindCommitWithTrailerAsync(string worktree, string trailer, CancellationToken ct)
    {
        // %x1f separates sha from message, %x1e separates commits.
        var log = await RunAsync(worktree, ct, "log", "-n", "200", "--format=%H%x1f%B%x1e");
        return log.Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Trim().Split('\x1f', 2))
            .Where(parts => parts.Length == 2 && parts[1].Split('\n').Any(line => line.Trim() == trailer))
            .Select(parts => parts[0])
            .FirstOrDefault();
    }

    /// <summary>
    /// Paths changed relative to HEAD (staged, unstaged and untracked), repo-relative with forward slashes.
    /// A rename reports its new path.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ChangedPathsAsync(string worktree, CancellationToken ct, params string[] pathspec)
    {
        // Not RunAsync: trimming would eat the leading status column of the first line.
        var result = await ExecAsync(worktree, ct, ["status", "--porcelain", "--untracked-files=all", "--", .. pathspec]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git status failed ({result.ExitCode}): {result.StdErr.Trim()}");
        }

        return result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Length > 3)
            .Select(line => line[3..].Trim())
            .Select(path => path.Contains(" -> ", StringComparison.Ordinal)
                ? path[(path.IndexOf(" -> ", StringComparison.Ordinal) + 4)..]
                : path)
            .Select(path => path.Trim('"'))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
