using Orchestrator.Activities.Infrastructure;
using Orchestrator.Activities.Telemetry;
using Orchestrator.Activities.Verification;
using Orchestrator.Contracts;
using Temporalio.Activities;

namespace Orchestrator.Activities;

public class VerifyActivities(ILangfuseScores scores)
{
    /// <summary>
    /// A story passes only if: no locked file was touched, the build succeeds, every story test exists and
    /// passes, and the full suite passes apart from the tests of stories not reached yet. Build and test output go to one raw log outside the worktree.
    /// </summary>
    [Activity(ActivityNames.Verify)]
    public async Task<VerifyResult> VerifyAsync(VerifyInput input)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        OrchestratorTelemetry.TagStory(input.FeatureId, input.StoryId, input.Iteration);
        var paths = new RalphPaths(input.WorktreePath, input.FeatureId);
        var log = paths.LogFile(input.StoryId, input.Iteration, "verify.log");
        var trxDir = paths.LogFile(input.StoryId, input.Iteration, "trx");
        ResetOutputs(log, trxDir); // a retried attempt starts clean

        var locked = await FindLockedPathViolationsAsync(paths, ct);
        var newPackages = await PackageChanges.FindAddedAsync(paths.Worktree, ct);

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
        var failed = FailedOutsidePending(results, input.PendingTestIds);
        var storyTestsPassed = buildSucceeded && missing.Count == 0 && input.TestIds.All(id => results[id]);
        var fullSuitePassed = buildSucceeded && IsFullSuitePassed(results, input.PendingTestIds, testExit);
        var passed = locked.Count == 0 && buildSucceeded && storyTestsPassed && fullSuitePassed;

        var result = new VerifyResult(
            passed,
            buildSucceeded,
            storyTestsPassed,
            fullSuitePassed,
            locked,
            passed ? null : FailureSignature.From(locked, buildSucceeded, missing, failed, fullSuitePassed),
            log,
            newPackages);
        await RecordAsync(input, result, ct);
        return result;
    }

    private async Task RecordAsync(VerifyInput input, VerifyResult result, CancellationToken ct)
    {
        OrchestratorTelemetry.SetCurrentMetadata("passed", result.Passed);
        OrchestratorTelemetry.SetCurrentMetadata("buildSucceeded", result.BuildSucceeded);
        OrchestratorTelemetry.SetCurrentMetadata("storyTestsPassed", result.StoryTestsPassed);
        OrchestratorTelemetry.SetCurrentMetadata("fullSuitePassed", result.FullSuitePassed);
        OrchestratorTelemetry.SetCurrentMetadata("lockedPathViolations", result.LockedPathViolations.Count);
        OrchestratorTelemetry.SetCurrentMetadata("newPackages", string.Join(", ", result.NewPackages));
        OrchestratorTelemetry.SetCurrentMetadata("failureSignature", result.FailureSignature);
        await scores.PostAsync(
            "verify_passed", result.Passed ? 1 : 0, ScoreDataType.Boolean, $"{input.StoryId}-{input.Iteration}", onSpan: true, ct);
    }

    /// <summary>Failed tests, ignoring those of stories not reached yet.</summary>
    internal static List<string> FailedOutsidePending(IReadOnlyDictionary<string, bool> results, IReadOnlyList<string> pendingTestIds) =>
        results.Where(r => !r.Value && !pendingTestIds.Contains(r.Key)).Select(r => r.Key).ToList();

    /// <summary>
    /// Every test outside the pending set passed. A non-zero exit is accepted only when a pending test explains it;
    /// otherwise it means something the TRX does not show (a crash, a test project that did not run).
    /// </summary>
    internal static bool IsFullSuitePassed(
        IReadOnlyDictionary<string, bool> results, IReadOnlyList<string> pendingTestIds, int testExit) =>
        results.Count > 0
        && FailedOutsidePending(results, pendingTestIds).Count == 0
        && (testExit == 0 || results.Any(r => !r.Value && pendingTestIds.Contains(r.Key)));

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
