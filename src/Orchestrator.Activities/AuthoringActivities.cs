using System.Text.Json;
using System.Text.RegularExpressions;
using Orchestrator.Activities.Infrastructure;
using Orchestrator.Activities.Stories;
using Orchestrator.Activities.Telemetry;
using Orchestrator.Activities.Verification;
using Orchestrator.Contracts;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace Orchestrator.Activities;

/// <summary>
/// Turns a spec into stories.json and acceptance tests before the Ralph loop runs. The agent drafts both;
/// these activities write its prompts, check what it produced, and commit the result.
/// </summary>
public partial class AuthoringActivities
{
    private const string SpecTrailer = "Ralph-Spec";

    /// <summary>Copies the spec into specs/{featureId}/spec.md and stages it. Safe to retry: overwrites.</summary>
    [Activity(ActivityNames.ImportSpec)]
    public async Task ImportSpecAsync(ImportSpecInput input)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        if (!File.Exists(input.SpecFilePath))
        {
            throw new ApplicationFailureException(
                $"Spec file {input.SpecFilePath} not found.", errorType: "SpecNotFound", nonRetryable: true);
        }

        var paths = new RalphPaths(input.WorktreePath, input.FeatureId);
        Directory.CreateDirectory(paths.Abs(paths.SpecDirRel));
        File.Copy(input.SpecFilePath, paths.Abs(paths.SpecRel), overwrite: true);
        await Git.RunAsync(paths.Worktree, ct, "add", "--", paths.SpecRel);
    }

    /// <summary>
    /// Undoes agent edits to spec.md (restored from the index, where ImportSpec staged it) and writes
    /// STORIES.prompt.md with the feedback on the previous draft. Safe to retry: overwrites.
    /// </summary>
    [Activity(ActivityNames.PrepareStoriesDraft)]
    public async Task PrepareStoriesDraftAsync(PrepareStoriesDraftInput input)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var paths = new RalphPaths(input.WorktreePath, input.FeatureId);
        await Git.RunAsync(paths.Worktree, ct, "checkout", "--", paths.SpecRel);

        Directory.CreateDirectory(paths.Abs(paths.RalphDirRel));
        await File.WriteAllTextAsync(
            paths.Abs(paths.StoriesPromptRel), PromptRenderer.RenderStories(input.FeatureId, input.Draft, input.Feedback), ct);
    }

    /// <summary>
    /// Checks the drafted stories.json. Problems are returned, not thrown, so the workflow can feed them back to
    /// the agent. Also rejects edits to spec.md and files other than stories.json.
    /// </summary>
    [Activity(ActivityNames.ValidateStories)]
    public async Task<ValidateStoriesResult> ValidateStoriesAsync(ValidateStoriesInput input)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var paths = new RalphPaths(input.WorktreePath, input.FeatureId);
        var storiesPath = paths.Abs(paths.StoriesRel);
        var errors = new List<string>();

        var specEdited = await Git.RunAsync(paths.Worktree, ct, "diff", "--name-only", "--", paths.SpecRel);
        if (specEdited.Length > 0)
        {
            errors.Add($"{paths.SpecRel} was edited; it must not change.");
        }

        await ReservedNameFiles.RemoveAsync(paths.Worktree, ct); // `2>nul` debris is not the agent's work
        var others = (await Git.ChangedPathsAsync(paths.Worktree, ct))
            .Where(p => p != paths.SpecRel && p != paths.StoriesRel && !IsRalphPath(p))
            .ToList();
        if (others.Count > 0)
        {
            errors.Add($"Only {paths.StoriesRel} may be written, but these also changed: {string.Join(", ", others)}.");
        }

        IReadOnlyList<StorySummary> summaries = [];
        if (!File.Exists(storiesPath))
        {
            errors.Add($"{paths.StoriesRel} was not written.");
        }
        else
        {
            try
            {
                var document = StoriesFile.Read(storiesPath);
                errors.AddRange(StoriesFile.Validate(document, input.FeatureId));
                errors.AddRange(ValidateForReview(document));
                summaries = document.Stories?
                    .Where(s => !string.IsNullOrWhiteSpace(s.Id))
                    .OrderBy(s => s.Priority)
                    .ThenBy(s => s.Id, StringComparer.Ordinal)
                    .Select(s => new StorySummary(s.Id!, s.Title ?? "", s.Priority))
                    .ToList() ?? [];
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                errors.Add($"{paths.StoriesRel} is not valid JSON: {ex.Message}");
            }
        }

        OrchestratorTelemetry.SetCurrentMetadata("errors", errors.Count);
        OrchestratorTelemetry.SetCurrentMetadata("stories", summaries.Count);
        return new ValidateStoriesResult(errors, storiesPath, summaries);
    }

    /// <summary>
    /// Restores the approved spec.md and stories.json from HEAD (undoing agent edits) and writes TESTS.prompt.md.
    /// Earlier test drafts are kept so the agent can fix them. Safe to retry: overwrites.
    /// </summary>
    [Activity(ActivityNames.PrepareTestsDraft)]
    public async Task PrepareTestsDraftAsync(PrepareTestsDraftInput input)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var paths = new RalphPaths(input.WorktreePath, input.FeatureId);
        await Git.RunAsync(paths.Worktree, ct, "checkout", "HEAD", "--", paths.SpecRel, paths.StoriesRel);

        Directory.CreateDirectory(paths.Abs(paths.RalphDirRel));
        await File.WriteAllTextAsync(
            paths.Abs(paths.TestsPromptRel), PromptRenderer.RenderTests(input.FeatureId, input.Draft), ct);
    }

    /// <summary>
    /// The drafted tests pass only if: nothing outside the acceptance folder and solution files changed, the build
    /// succeeds, every story test id is found, every story test fails (nothing is implemented yet), and every
    /// other test passes. Build and test output go to one raw log outside the worktree.
    /// </summary>
    [Activity(ActivityNames.VerifyTests)]
    public async Task<VerifyTestsResult> VerifyTestsAsync(VerifyTestsInput input)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        OrchestratorTelemetry.TagStory(input.FeatureId, DraftIds.Tests, input.Draft);
        var paths = new RalphPaths(input.WorktreePath, input.FeatureId);
        var log = paths.LogFile(DraftIds.Tests, input.Draft, "verify.log");
        var trxDir = paths.LogFile(DraftIds.Tests, input.Draft, "trx");
        ResetOutputs(log, trxDir); // a retried attempt starts clean

        var violations = await FindTestDraftViolationsAsync(paths, ct);
        var storyTestIds = StoriesFile.Read(paths.Abs(paths.StoriesRel)).Stories!
            .SelectMany(s => s.TestIds ?? [])
            .Distinct()
            .ToList();

        var buildExit = await ProcessRunner.RunToFileAsync(
            "dotnet", ["build", "--nologo"], paths.Worktree, log, stdErrPath: null, ct);
        var buildSucceeded = buildExit == 0;

        IReadOnlyDictionary<string, bool> results = new Dictionary<string, bool>();
        if (buildSucceeded)
        {
            // A non-zero exit is expected: every story test should fail. The TRX results decide.
            await ProcessRunner.RunToFileAsync(
                "dotnet",
                ["test", "--no-build", "--nologo", "--logger", "trx", "--results-directory", trxDir],
                paths.Worktree, log, stdErrPath: null, ct);
            results = TrxParser.ParseDirectory(trxDir);
        }

        var evaluation = EvaluateTests(results, storyTestIds);
        var passed = violations.Count == 0 && buildSucceeded && results.Count > 0 && evaluation.IsRed;
        var result = new VerifyTestsResult(
            passed,
            buildSucceeded,
            violations,
            buildSucceeded ? evaluation.Missing : [],
            evaluation.Passing,
            evaluation.Broken,
            passed ? null : TestsFailureSignature(violations, buildSucceeded, results.Count, evaluation),
            log);

        OrchestratorTelemetry.SetCurrentMetadata("passed", passed);
        OrchestratorTelemetry.SetCurrentMetadata("buildSucceeded", buildSucceeded);
        OrchestratorTelemetry.SetCurrentMetadata("failureSignature", result.FailureSignature);
        return result;
    }

    /// <summary>
    /// Commits one authoring stage on the spec branch: spec.md and stories.json, or the acceptance folder and
    /// changed solution files. Nothing under .ralph is committed. Idempotent: the commit carries a
    /// "Ralph-Spec: {workflowId}/{stage}" trailer, and a retry returns the existing commit instead.
    /// </summary>
    [Activity(ActivityNames.CommitSpec)]
    public async Task<CommitSpecResult> CommitSpecAsync(CommitSpecInput input)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var paths = new RalphPaths(input.WorktreePath, input.FeatureId);
        var trailer = $"{SpecTrailer}: {paths.WorkflowId}/{input.Stage}";

        if (await Git.FindCommitWithTrailerAsync(paths.Worktree, trailer, ct) is { } existing)
        {
            return new CommitSpecResult(existing);
        }

        await ReservedNameFiles.RemoveAsync(paths.Worktree, ct);
        string subject;
        if (input.Stage == SpecCommitStage.Stories)
        {
            await Git.RunAsync(paths.Worktree, ct, "add", "--", paths.SpecRel, paths.StoriesRel);
            subject = $"docs({input.FeatureId}): add spec and approved stories";
        }
        else
        {
            var solutionFiles = (await Git.ChangedPathsAsync(paths.Worktree, ct)).Where(IsSolutionFile);
            await Git.RunAsync(paths.Worktree, ct, ["add", "--", paths.AcceptanceDirRel, .. solutionFiles]);
            subject = $"test({input.FeatureId}): add acceptance tests";
        }

        return new CommitSpecResult(await Git.CommitAsOrchestratorAsync(paths.Worktree, ct, subject, trailer));
    }

    /// <summary>Changed paths a test draft may not touch: everything except the acceptance folder, solution files and .ralph.</summary>
    internal static async Task<IReadOnlyList<string>> FindTestDraftViolationsAsync(RalphPaths paths, CancellationToken ct)
    {
        await ReservedNameFiles.RemoveAsync(paths.Worktree, ct); // `2>nul` debris is not the agent's work
        return (await Git.ChangedPathsAsync(paths.Worktree, ct))
            .Where(p => !p.StartsWith(paths.AcceptanceDirRel + "/", StringComparison.Ordinal)
                        && !IsSolutionFile(p)
                        && !IsRalphPath(p))
            .ToList();
    }

    internal sealed record TestsEvaluation(
        IReadOnlyList<string> Missing, IReadOnlyList<string> Passing, IReadOnlyList<string> Broken)
    {
        public bool IsRed => Missing.Count == 0 && Passing.Count == 0 && Broken.Count == 0;
    }

    /// <summary>
    /// Before implementation, every story test must exist and fail, and every other test must pass. A failing test
    /// that no story lists is broken: either an existing test the draft broke, or an extra test nobody will implement.
    /// </summary>
    internal static TestsEvaluation EvaluateTests(IReadOnlyDictionary<string, bool> results, IReadOnlyList<string> storyTestIds) =>
        new(
            storyTestIds.Where(id => !results.ContainsKey(id)).Order(StringComparer.Ordinal).ToList(),
            storyTestIds.Where(id => results.TryGetValue(id, out var ok) && ok).Order(StringComparer.Ordinal).ToList(),
            results.Where(r => !r.Value && !storyTestIds.Contains(r.Key)).Select(r => r.Key).Order(StringComparer.Ordinal).ToList());

    /// <summary>Extra checks on a drafted stories.json, beyond what LoadStories needs, so a reviewer gets usable stories.</summary>
    internal static IReadOnlyList<string> ValidateForReview(StoriesDocument document)
    {
        var errors = new List<string>();
        foreach (var story in document.Stories ?? [])
        {
            if (string.IsNullOrWhiteSpace(story.Id))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(story.Title))
            {
                errors.Add($"story {story.Id} has no title.");
            }

            if (story.Priority < 1)
            {
                errors.Add($"story {story.Id} must have a priority of 1 or more.");
            }

            if (story.AcceptanceCriteria is not { Count: > 0 } || story.AcceptanceCriteria.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add($"story {story.Id} must have at least one non-empty acceptance criterion.");
            }

            errors.AddRange((story.TestIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id) && !TestIdPattern().IsMatch(id))
                .Select(id => $"story {story.Id} test id '{id}' is not a Namespace.Class.Method name."));
        }

        return errors;
    }

    private static string TestsFailureSignature(
        IReadOnlyList<string> violations, bool buildSucceeded, int resultCount, TestsEvaluation evaluation)
    {
        var signature = FailureSignature.From(
            violations, buildSucceeded, evaluation.Missing, evaluation.Broken, fullSuitePassed: resultCount > 0) ?? "";
        if (buildSucceeded && evaluation.Passing.Count > 0)
        {
            signature += (signature.Length > 0 ? ";" : "") + "passing:" + string.Join('|', evaluation.Passing);
        }

        return signature.Length <= FailureSignature.MaxLength ? signature : signature[..FailureSignature.MaxLength];
    }

    private static bool IsRalphPath(string path) => path.StartsWith(".ralph/", StringComparison.Ordinal);

    private static bool IsSolutionFile(string path) =>
        !path.Contains('/') && (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                                || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));

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

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*){2,}$")]
    private static partial Regex TestIdPattern();
}
