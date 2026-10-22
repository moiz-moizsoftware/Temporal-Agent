using Temporalio.Testing;

namespace Orchestrator.Workflows.Tests;

/// <summary>One time-skipping test server shared by all tests in a class.</summary>
public sealed class WorkflowEnvironmentFixture : IAsyncLifetime
{
    public WorkflowEnvironment Env { get; private set; } = null!;

    public async Task InitializeAsync() => Env = await WorkflowEnvironment.StartTimeSkippingAsync();

    public async Task DisposeAsync() => await Env.ShutdownAsync();
}
