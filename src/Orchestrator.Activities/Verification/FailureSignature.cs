namespace Orchestrator.Activities.Verification;

/// <summary>
/// A short, stable, human-readable description of why verification failed. The workflow compares
/// consecutive signatures to detect a stalled story, so the same failure must always give the same string.
/// </summary>
public static class FailureSignature
{
    public const int MaxLength = 200;

    public static string? From(
        IReadOnlyCollection<string> lockedViolations,
        bool buildSucceeded,
        IReadOnlyCollection<string> missingTests,
        IReadOnlyCollection<string> failedTests,
        bool fullSuitePassed)
    {
        var parts = new List<string>();
        if (lockedViolations.Count > 0)
        {
            parts.Add("locked:" + Join(lockedViolations));
        }

        if (!buildSucceeded)
        {
            parts.Add("build-failed");
        }
        else
        {
            if (missingTests.Count > 0)
            {
                parts.Add("missing:" + Join(missingTests));
            }

            if (failedTests.Count > 0)
            {
                parts.Add("failed:" + Join(failedTests));
            }
            else if (!fullSuitePassed)
            {
                parts.Add("tests-failed");
            }
        }

        if (parts.Count == 0)
        {
            return null;
        }

        var signature = string.Join(';', parts);
        return signature.Length <= MaxLength ? signature : signature[..MaxLength];
    }

    private static string Join(IEnumerable<string> items) =>
        string.Join('|', items.Distinct().Order(StringComparer.Ordinal));
}
