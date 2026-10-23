using ShoppyAI.Core.Workspace;

namespace ShoppyAI.Tests;

public sealed class WorkspaceProcessRunnerTests : IDisposable
{
    private readonly string _workspace = TestData.NewTempDirectory();

    [Fact]
    public async Task Captures_output_and_exit_code_in_the_explicit_workspace()
    {
        var runner = new WorkspaceProcessRunner(_workspace);

        var result = await runner.RunAsync("git", ["--version"], _workspace, TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("git version", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task Returns_nonzero_exit_code_and_stderr_without_throwing_away_diagnostics()
    {
        var runner = new WorkspaceProcessRunner(_workspace);

        var result = await runner.RunAsync("git", ["not-a-real-command"], _workspace, TimeSpan.FromSeconds(10));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not a git command", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Drains_large_stdout_and_stderr_concurrently()
    {
        var runner = new WorkspaceProcessRunner(_workspace);
        var (fileName, args) = OutputFloodCommand();

        var result = await runner.RunAsync(fileName, args, _workspace, TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1_000_000, result.StandardOutput.Length);
        Assert.Equal(1_000_000, result.StandardError.Length);
    }

    [Fact]
    public async Task Refuses_working_directories_outside_the_workspace()
    {
        var runner = new WorkspaceProcessRunner(_workspace);
        var outside = TestData.NewTempDirectory();

        try
        {
            await Assert.ThrowsAsync<WorkspaceAccessException>(() =>
                runner.RunAsync("git", ["--version"], outside, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Times_out_and_terminates_a_long_running_child()
    {
        var runner = new WorkspaceProcessRunner(_workspace);
        var (fileName, args) = LongRunningCommand();

        var result = await runner.RunAsync(fileName, args, _workspace, TimeSpan.FromMilliseconds(250));

        Assert.True(result.TimedOut);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Cancellation_terminates_a_long_running_child()
    {
        var runner = new WorkspaceProcessRunner(_workspace);
        var (fileName, args) = LongRunningCommand();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(fileName, args, _workspace, TimeSpan.FromSeconds(30), cancellation.Token));
    }

    private static (string FileName, string[] Arguments) LongRunningCommand() => OperatingSystem.IsWindows()
        ? ("cmd.exe", ["/c", "ping -n 30 127.0.0.1 > nul"])
        : ("/bin/sh", ["-c", "sleep 30"]);

    private static (string FileName, string[] Arguments) OutputFloodCommand() => OperatingSystem.IsWindows()
        ? ("powershell.exe", ["-NoProfile", "-Command", "$x = 'x' * 1000000; [Console]::Out.Write($x); [Console]::Error.Write($x)"])
        : ("/bin/sh", ["-c", "head -c 1000000 /dev/zero | tr '\\0' x; head -c 1000000 /dev/zero | tr '\\0' x >&2"]);

    public void Dispose() => Directory.Delete(_workspace, recursive: true);
}
