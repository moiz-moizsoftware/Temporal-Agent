using System.Text;
using CliWrap;
using Temporalio.Activities;

namespace Orchestrator.Activities.Infrastructure;

/// <summary>Runs external processes via CliWrap. Heartbeats while running when inside an activity.</summary>
internal static class ProcessRunner
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    public sealed record Captured(int ExitCode, string StdOut, string StdErr);

    /// <summary>Runs and captures output in memory. For short commands with small output (git).</summary>
    public static async Task<Captured> RunCapturedAsync(
        string fileName, IEnumerable<string> args, string workingDirectory, CancellationToken ct)
    {
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        var result = await Command(fileName, args, workingDirectory)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOut))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErr))
            .ExecuteAsync(ct);
        return new Captured(result.ExitCode, stdOut.ToString(), stdErr.ToString());
    }

    /// <summary>
    /// Runs and streams output to files. With no <paramref name="stdErrPath"/>, stderr is merged into the
    /// stdout file. Appends when the file exists so several commands can share one log.
    /// </summary>
    public static async Task<int> RunToFileAsync(
        string fileName,
        IEnumerable<string> args,
        string workingDirectory,
        string stdOutPath,
        string? stdErrPath,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(stdOutPath)!);
        await using var stdOut = new StreamWriter(stdOutPath, append: true) { AutoFlush = true };
        await using var stdErr = stdErrPath is null ? null : new StreamWriter(stdErrPath, append: true) { AutoFlush = true };
        var gate = new object();

        void Write(StreamWriter writer, string line)
        {
            lock (gate)
            {
                writer.WriteLine(line);
            }
        }

        var command = Command(fileName, args, workingDirectory)
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line => Write(stdOut, line)))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line => Write(stdErr ?? stdOut, line)));

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = HeartbeatUntilCancelledAsync(heartbeatCts.Token);
        try
        {
            var result = await command.ExecuteAsync(ct);
            return result.ExitCode;
        }
        finally
        {
            await heartbeatCts.CancelAsync();
            await heartbeat;
        }
    }

    private static Command Command(string fileName, IEnumerable<string> args, string workingDirectory) =>
        Cli.Wrap(fileName)
            .WithArguments(args)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None);

    private static async Task HeartbeatUntilCancelledAsync(CancellationToken ct)
    {
        if (!ActivityExecutionContext.HasCurrent)
        {
            return;
        }

        var context = ActivityExecutionContext.Current;
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                context.Heartbeat();
            }
        }
        catch (OperationCanceledException)
        {
            // Process finished or activity cancelled.
        }
    }
}
