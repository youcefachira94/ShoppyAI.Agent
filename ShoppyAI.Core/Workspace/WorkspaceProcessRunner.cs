using System.ComponentModel;
using System.Diagnostics;

namespace ShoppyAI.Core.Workspace;

internal sealed record ProcessExecutionResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>Runs a caller-selected executable without a shell, confined to the workspace.</summary>
internal sealed class WorkspaceProcessRunner
{
    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public WorkspaceProcessRunner(string workspaceRoot)
    {
        var workspace = new WorkspaceFileService(workspaceRoot);
        _root = workspace.Root;
        _rootPrefix = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
    }

    public async Task<ProcessExecutionResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "A finite positive process timeout is required.");

        var cwd = ValidateWorkingDirectory(workingDirectory);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        if (!process.Start())
            throw new InvalidOperationException($"Could not start process '{fileName}'.");

        // Drain both pipes before waiting so a full stderr/stdout buffer cannot deadlock the child.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
                // The process may have exited between cancellation and Kill.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);

            timedOut = true;
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        return new ProcessExecutionResult(process.ExitCode, stdoutTask.Result, stderrTask.Result, timedOut);
    }

    private string ValidateWorkingDirectory(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (!Path.IsPathRooted(workingDirectory))
            throw new WorkspaceAccessException("A process working directory must be an absolute workspace path.");

        var fullPath = Path.GetFullPath(workingDirectory);
        if (!fullPath.StartsWith(_rootPrefix, _pathComparison) && !string.Equals(fullPath, _root, _pathComparison))
            throw new WorkspaceAccessException("The process working directory must remain inside the selected workspace.");
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException("The process working directory does not exist.");

        var relative = Path.GetRelativePath(_root, fullPath);
        var current = _root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new WorkspaceAccessException("A process cannot start through a symbolic link or junction.");
        }

        return fullPath;
    }
}
