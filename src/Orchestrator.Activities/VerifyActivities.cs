using Orchestrator.Activities.Infrastructure;
using Orchestrator.Activities.Verification;
using Orchestrator.Contracts;
using Temporalio.Activities;

namespace Orchestrator.Activities;

public class VerifyActivities
{
    /// <summary>
    /// A story passes only if: no locked file was touched, the build succeeds, every story test exists and
    /// passes, and the full suite passes. Build and test output go to one raw log outside the worktree.
    /// </summary>
    [Activity(ActivityNames.Verify)]
    public async Task<VerifyResult> VerifyAsync(VerifyInput input)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var paths = new RalphPaths(input.WorktreePath, input.FeatureId);
        var log = paths.LogFile(input.StoryId, input.Iteration, "verify.log");
        var trxDir = paths.LogFile(input.StoryId, input.Iteration, "trx");
        ResetOutputs(log, trxDir); // a retried attempt starts clean

        var locked = await FindLockedPathViolationsAsync(paths, ct);

        var buildExit = await ProcessRunner.RunToFileAsync(
            "dotnet", ["build", "--nologo"], paths.Worktree, log, stdErrPath: null, ct);
        var buildSucceeded = buildExit == 0;

        IReadOnlyDictionary<string, bool> results = new Dictionary<string, bool>();
        var testExit = -1;
        if (buildSucceeded)
        {
            testExit = await ProcessRunner.RunToFileAsync(
                "dotnet",
                ["test", "--no-build", "--nologo", "--logger", "trx", "--results-directory", trxDir],
                paths.Worktree, log, stdErrPath: null, ct);
            results = TrxParser.ParseDirectory(trxDir);
        }

        var missing = input.TestIds.Where(id => !results.ContainsKey(id)).ToList();
        var failed = results.Where(r => !r.Value).Select(r => r.Key).ToList();
        var storyTestsPassed = buildSucceeded && missing.Count == 0 && input.TestIds.All(id => results[id]);
        var fullSuitePassed = buildSucceeded && testExit == 0 && results.Count > 0 && failed.Count == 0;
        var passed = locked.Count == 0 && buildSucceeded && storyTestsPassed && fullSuitePassed;

        return new VerifyResult(
            passed,
            buildSucceeded,
            storyTestsPassed,
            fullSuitePassed,
            locked,
            passed ? null : FailureSignature.From(locked, buildSucceeded, missing, failed, fullSuitePassed),
            log);
    }

    /// <summary>
    /// Locked = anything under specs/{featureId} (vs HEAD, including new files), plus the orchestrator-owned
    /// status.json and last_failure.md (working tree vs index; WriteLoopFiles stages them before the agent runs).
    /// </summary>
    internal static async Task<IReadOnlyList<string>> FindLockedPathViolationsAsync(RalphPaths paths, CancellationToken ct)
    {
        // Not Git.RunAsync: trimming would eat the leading status column of the first line.
        var status = await Git.ExecAsync(
            paths.Worktree, ct, "status", "--porcelain", "--untracked-files=all", "--", paths.SpecDirRel);
        var specChanges = status.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Length > 3)
            .Select(line => line[3..].Trim().Trim('"'));

        var ownedChanges = (await Git.RunAsync(
                paths.Worktree, ct, "diff", "--name-only", "--", paths.StatusRel, paths.LastFailureRel))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim());

        return specChanges.Concat(ownedChanges).Distinct().Order(StringComparer.Ordinal).ToList();
    }

    private static void ResetOutputs(string log, string trxDir)
    {
        if (File.Exists(log))
        {
            File.Delete(log);
        }

        if (Directory.Exists(trxDir))
        {
            Directory.Delete(trxDir, recursive: true);
        }
    }
}
