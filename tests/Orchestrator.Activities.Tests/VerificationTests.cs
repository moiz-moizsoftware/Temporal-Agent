using System.Xml.Linq;
using Orchestrator.Activities.Verification;

namespace Orchestrator.Activities.Tests;

public class VerificationTests
{
    private const string Trx =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testId="a" testName="Hello.Tests.GreeterTests.GreetsByName" outcome="Passed" />
            <UnitTestResult testId="b" testName="Hello.Tests.GreeterTests.SaysBye" outcome="Failed" />
            <UnitTestResult testId="c1" testName="Hello.Tests.MathTests.Adds(x: 1)" outcome="Passed" />
            <UnitTestResult testId="c2" testName="Hello.Tests.MathTests.Adds(x: 2)" outcome="Failed" />
          </Results>
          <TestDefinitions>
            <UnitTest id="a" name="GreetsByName"><TestMethod className="Hello.Tests.GreeterTests" name="GreetsByName" /></UnitTest>
            <UnitTest id="b" name="SaysBye"><TestMethod className="Hello.Tests.GreeterTests, Hello.Tests" name="SaysBye" /></UnitTest>
            <UnitTest id="c1" name="Adds"><TestMethod className="Hello.Tests.MathTests" name="Adds" /></UnitTest>
            <UnitTest id="c2" name="Adds"><TestMethod className="Hello.Tests.MathTests" name="Adds" /></UnitTest>
          </TestDefinitions>
        </TestRun>
        """;

    [Fact]
    public void Trx_MapsMethodsToOutcome()
    {
        var results = TrxParser.Parse(XDocument.Parse(Trx));

        Assert.Equal(3, results.Count);
        Assert.True(results["Hello.Tests.GreeterTests.GreetsByName"]);
        Assert.False(results["Hello.Tests.GreeterTests.SaysBye"]);
        Assert.False(results["Hello.Tests.MathTests.Adds"]); // one theory case failed
    }

    [Fact]
    public void Trx_MissingDirectoryGivesNoResults() =>
        Assert.Empty(TrxParser.ParseDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())));

    [Fact]
    public void Signature_IsSortedAndStable()
    {
        var a = FailureSignature.From([], true, [], ["B.Test", "A.Test", "B.Test"], fullSuitePassed: false);
        var b = FailureSignature.From([], true, [], ["A.Test", "B.Test"], fullSuitePassed: false);

        Assert.Equal("failed:A.Test|B.Test", a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Signature_CombinesLockedAndBuild() =>
        Assert.Equal(
            "locked:specs/f1/spec.md;build-failed",
            FailureSignature.From(["specs/f1/spec.md"], buildSucceeded: false, ["X.Y"], [], fullSuitePassed: false));

    [Fact]
    public void Signature_ReportsMissingTestsAndSuiteFailure() =>
        Assert.Equal("missing:X.Y;tests-failed", FailureSignature.From([], true, ["X.Y"], [], fullSuitePassed: false));

    [Fact]
    public void Signature_IsCapped() =>
        Assert.Equal(
            FailureSignature.MaxLength,
            FailureSignature.From([], true, [], Enumerable.Range(0, 50).Select(i => $"Some.Long.Test{i:00}").ToList(), false)!.Length);

    [Fact]
    public void FullSuite_IgnoresFailingPendingTests()
    {
        var results = new Dictionary<string, bool> { ["A.S1"] = true, ["A.S2"] = false };

        Assert.True(VerifyActivities.IsFullSuitePassed(results, ["A.S2"], testExit: 1));
        Assert.Empty(VerifyActivities.FailedOutsidePending(results, ["A.S2"]));
    }

    [Fact]
    public void FullSuite_FailsOnNonPendingFailure()
    {
        var results = new Dictionary<string, bool> { ["A.S1"] = true, ["A.Old"] = false, ["A.S2"] = false };

        Assert.False(VerifyActivities.IsFullSuitePassed(results, ["A.S2"], testExit: 1));
        Assert.Equal(["A.Old"], VerifyActivities.FailedOutsidePending(results, ["A.S2"]));
    }

    [Fact]
    public void FullSuite_FailsOnUnexplainedNonZeroExit() =>
        Assert.False(VerifyActivities.IsFullSuitePassed(new Dictionary<string, bool> { ["A.S1"] = true }, ["A.S2"], testExit: 1));

    [Fact]
    public void Packages_ParsesReferencesAndCentralVersions() =>
        Assert.Equal(
            ["Polly@8.5.0", "Serilog@4.0.0", "Dapper"],
            PackageChanges.Parse(
                """
                <Project>
                  <ItemGroup>
                    <PackageReference Include="Polly" Version="8.5.0" />
                    <PackageVersion Include="Serilog"><Version>4.0.0</Version></PackageVersion>
                    <PackageReference Include="Dapper" />
                    <ProjectReference Include="..\Other.csproj" />
                  </ItemGroup>
                </Project>
                """));

    [Fact]
    public void Packages_InvalidXmlGivesNone() => Assert.Empty(PackageChanges.Parse("<Project>"));

    [Fact]
    public void Signature_NullWhenEverythingPasses() =>
        Assert.Null(FailureSignature.From([], true, [], [], fullSuitePassed: true));
}
