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
}
