namespace Orchestrator.Activities.Infrastructure;

/// <summary>
/// The agent's Bash tool runs in a POSIX shell on Windows, where `cmd 2>nul` creates a real file named "nul".
/// Windows reserves that name (and CON, PRN, AUX, COM1-9, LPT1-9), so git cannot index it ("short read while
/// indexing nul") and normal file APIs cannot delete it. Such files are never real work, so they are deleted
/// through the \\?\ prefix, which bypasses the reserved-name check.
/// </summary>
internal static class ReservedNameFiles
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static bool IsReserved(string relativePath) =>
        Reserved.Contains(Path.GetFileNameWithoutExtension(relativePath.TrimEnd('/', '\\')));

    /// <summary>Deletes untracked or changed reserved-name files in the worktree. Returns the paths removed.</summary>
    public static async Task<IReadOnlyList<string>> RemoveAsync(string worktree, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var removed = new List<string>();
        foreach (var path in (await Git.ChangedPathsAsync(worktree, ct)).Where(IsReserved))
        {
            // Not Path.GetFullPath: it maps ".../nul" to the device "\\.\nul". \\?\ needs an absolute, backslashed path.
            var extended = @"\\?\" + Path.Combine(Path.GetFullPath(worktree), path.Replace('/', '\\'));
            if (File.Exists(extended))
            {
                File.Delete(extended);
                removed.Add(path);
            }
        }

        return removed;
    }
}
