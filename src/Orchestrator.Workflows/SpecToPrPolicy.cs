using Orchestrator.Contracts;

namespace Orchestrator.Workflows;

/// <summary>Pure text and naming rules for SpecToPrWorkflow. No Temporal APIs, no side effects.</summary>
public static class SpecToPrPolicy
{
    public static string SpecBranch(string featureId) => $"spec/{featureId}";

    public static string ImplementationWorkflowId(string parentWorkflowId) => $"{parentWorkflowId}-impl";

    /// <summary>Feedback for the next stories draft after ValidateStories found problems.</summary>
    public static string InvalidStoriesFeedback(IReadOnlyList<string> errors) =>
        "The previous draft was rejected automatically because stories.json is invalid. Fix every problem:\n"
        + string.Join('\n', errors.Select(e => $"- {e}"));

    /// <summary>Feedback for the next stories draft after a reviewer rejected it.</summary>
    public static string RejectedStoriesFeedback(string? comment) =>
        "A reviewer rejected the previous draft. Their comment:\n\n"
        + (string.IsNullOrWhiteSpace(comment) ? "(no comment given; reconsider the split and the acceptance criteria)" : comment.Trim());

    /// <summary>
    /// last_failure.md body for a test draft that built but failed verification. A build failure is summarized
    /// from the raw log instead, so this returns null for it.
    /// </summary>
    public static string? TestsFeedback(VerifyTestsResult result)
    {
        if (!result.BuildSucceeded)
        {
            return null;
        }

        var problems = new List<string>();
        if (result.PathViolations.Count > 0)
        {
            problems.Add("Files outside the acceptance folder were changed. Revert them: " + string.Join(", ", result.PathViolations));
        }

        if (result.MissingTests.Count > 0)
        {
            problems.Add("These test ids were not found in the test results. Write them with exactly these names: "
                         + string.Join(", ", result.MissingTests));
        }

        if (result.PassingTests.Count > 0)
        {
            problems.Add("These tests already pass, so they cannot show the story was implemented. Make them check the new "
                         + "behaviour: " + string.Join(", ", result.PassingTests));
        }

        if (result.BrokenTests.Count > 0)
        {
            problems.Add("These tests fail but belong to no story. Fix them if they existed before, or remove them: "
                         + string.Join(", ", result.BrokenTests));
        }

        if (problems.Count == 0)
        {
            problems.Add("No test results were produced. Make sure the acceptance test project is in the solution.");
        }

        return "## What failed\n" + string.Join('\n', problems.Select(p => $"- {p}"))
               + "\n\n## What to try next\nFix only the problems above. Keep the tests that are already correct.";
    }
}
