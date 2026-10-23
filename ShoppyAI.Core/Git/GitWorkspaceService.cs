using System.Text.RegularExpressions;
using ShoppyAI.Core.Workspace;

namespace ShoppyAI.Core.Git;

public interface IGitActionApproval
{
    Task<bool> ApproveCommitAsync(string branch, string message, string stagedDiff, CancellationToken cancellationToken = default);
    Task<bool> ApprovePushAsync(string remote, string destination, string branch, IReadOnlyList<string> commits, CancellationToken cancellationToken = default);
}

/// <summary>Git operations scoped to one workspace, with explicit staging and separate commit/push approval.</summary>
public sealed class GitWorkspaceService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(45);
    private static readonly Regex PrivateKey = new("-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ProviderToken = new(@"\b(?:AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{30,}|sk-[A-Za-z0-9_-]{24,})\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CredentialAssignment = new(
        "(?im)\\b(?:password|passwd|secret|api[_-]?key|access[_-]?token)\\s*[:=]\\s*[^\\s]{12,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FilterConfigKey = new(
        @"^filter\.[A-Za-z0-9._-]+\.(?:clean|smudge|process)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ScpStyleRemote = new(
        @"^(?:[A-Za-z0-9._-]+@)?[A-Za-z0-9.-]+:[^:\s].*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UrlCredentials = new(
        @"(?i)(https?://)[^/\s@]+@", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly WorkspaceProcessRunner _runner;
    private readonly WorkspaceFileService _files;
    private readonly string _disabledHooksPath = Path.Combine(Path.GetTempPath(), "shoppyai-disabled-hooks-" + Guid.NewGuid().ToString("N"));

    public GitWorkspaceService(string workspaceRoot)
    {
        _files = new WorkspaceFileService(workspaceRoot);
        _runner = new WorkspaceProcessRunner(_files.Root);
    }

    public Task<string> StatusAsync(CancellationToken cancellationToken = default) => GitOutputAsync(["status", "--short", "--branch"], cancellationToken);
    public Task<string> DiffAsync(CancellationToken cancellationToken = default) => GitOutputAsync(["--no-pager", "diff", "--no-ext-diff", "--no-textconv"], cancellationToken);
    public Task<string> LogAsync(CancellationToken cancellationToken = default) => GitOutputAsync(["log", "--oneline", "-10"], cancellationToken);

    public async Task StageFilesAsync(IReadOnlyList<string> selectedPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedPaths);
        if (selectedPaths.Count == 0)
            throw new ArgumentException("Select at least one file to stage.", nameof(selectedPaths));

        var validated = selectedPaths.Select(path =>
        {
            var fullPath = _files.ValidateExistingFile(path);
            if (Directory.Exists(fullPath))
                throw new WorkspaceAccessException("Only explicitly selected files can be staged.");
            return Path.GetRelativePath(_files.Root, fullPath);
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        await RunGitAsync(["--literal-pathspecs", "add", "--", .. validated], cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> CommitAsync(string message, IGitActionApproval approval, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(approval);
        var branch = await CurrentBranchAsync(cancellationToken).ConfigureAwait(false);
        EnsureSafeBranch(branch);

        var namesResult = await RunGitAsync(["diff", "--cached", "--name-only", "-z"], cancellationToken).ConfigureAwait(false);
        var stagedPaths = namesResult.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (stagedPaths.Length == 0)
            throw new InvalidOperationException("There are no staged files to commit.");

        foreach (var path in stagedPaths)
            _files.ValidateRelativePath(path);

        var diff = await GitOutputAsync(["--no-pager", "diff", "--cached", "--binary", "--no-ext-diff", "--no-textconv", "--no-color"], cancellationToken).ConfigureAwait(false);
        if (diff.Contains("GIT binary patch", StringComparison.Ordinal))
            throw new InvalidOperationException("Binary staged files cannot be reviewed or secret-scanned safely; remove them from the staged set before committing.");
        if (ContainsPotentialSecret(diff))
            throw new InvalidOperationException("The staged diff contains a value matching a known secret pattern. Review and remove it before committing.");

        if (!await approval.ApproveCommitAsync(branch, message, diff, cancellationToken).ConfigureAwait(false))
            return false;

        await RunGitAsync(["commit", "-m", message], cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> PushAsync(IGitActionApproval approval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var branch = await CurrentBranchAsync(cancellationToken).ConfigureAwait(false);
        EnsureSafeBranch(branch);

        var upstreamResult = await RunGitAsync(["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"],
            cancellationToken, allowedExitCodes: [0, 128]).ConfigureAwait(false);
        var upstream = upstreamResult.StandardOutput.Trim();
        var slash = upstream.IndexOf('/');
        if (upstreamResult.ExitCode != 0 || slash <= 0 || slash == upstream.Length - 1)
            throw new InvalidOperationException("Push is blocked until this branch has an explicit upstream remote.");

        var remote = upstream[..slash];
        var remoteBranch = upstream[(slash + 1)..];
        var remoteUrl = await GitOutputAsync(["remote", "get-url", "--push", remote], cancellationToken).ConfigureAwait(false);
        var destination = RedactRemoteCredentials(remoteUrl.Trim());
        EnsureSupportedPushDestination(destination);
        var commitsOutput = await GitOutputAsync(["log", "--oneline", $"@{{upstream}}..HEAD"], cancellationToken).ConfigureAwait(false);
        var commits = commitsOutput.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        if (commits.Length == 0)
            throw new InvalidOperationException("There are no outgoing commits to push.");

        if (!await approval.ApprovePushAsync(remote, destination, remoteBranch, commits, cancellationToken).ConfigureAwait(false))
            return false;

        await RunGitAsync(["push", "--porcelain", remote, $"HEAD:refs/heads/{remoteBranch}"], cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<string> CurrentBranchAsync(CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(["symbolic-ref", "--quiet", "--short", "HEAD"],
            cancellationToken, allowedExitCodes: [0, 1]).ConfigureAwait(false);
        var branch = result.StandardOutput.Trim();
        if (result.ExitCode != 0 || branch.Length == 0)
            throw new InvalidOperationException("Git operation refused: checkout is detached.");
        return branch;
    }

    private static void EnsureSafeBranch(string branch)
    {
        if (branch.Equals("main", StringComparison.OrdinalIgnoreCase) ||
            branch.Equals("master", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Git mutations are blocked on main/master. Switch to a feature branch first.");
    }

    private static bool ContainsPotentialSecret(string diff) =>
        PrivateKey.IsMatch(diff) || ProviderToken.IsMatch(diff) || CredentialAssignment.IsMatch(diff);

    private static string RedactRemoteCredentials(string remote)
    {
        if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            var builder = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
            return builder.Uri.AbsoluteUri;
        }

        var at = remote.IndexOf('@');
        var colon = remote.IndexOf(':', Math.Max(0, at));
        if (at > 0 && colon > at)
            return remote[(at + 1)..];
        return remote;
    }

    private static void EnsureSupportedPushDestination(string destination)
    {
        if (Uri.TryCreate(destination, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeSsh || uri.IsFile))
            return;

        if (Path.IsPathRooted(destination) || ScpStyleRemote.IsMatch(destination))
            return;

        throw new InvalidOperationException("Push is blocked for custom Git transports. Configure a standard HTTPS, SSH, or local file remote.");
    }

    private async Task<string> GitOutputAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(arguments, cancellationToken).ConfigureAwait(false);
        return result.StandardOutput;
    }

    private async Task<WorkspaceProcessResult> RunGitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyCollection<int>? allowedExitCodes = null)
    {
        var filters = await _runner.RunAsync("git",
            ["config", "--name-only", "--get-regexp", @"^filter\..*\.(clean|smudge|process)$"],
            _files.Root, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (filters.TimedOut || (filters.ExitCode != 0 && filters.ExitCode != 1))
            throw new InvalidOperationException("Could not inspect repository filter configuration safely.");

        var safeArguments = new List<string>
        {
            "-c", $"core.hooksPath={_disabledHooksPath}",
            "-c", "core.fsmonitor=false",
            "-c", "commit.gpgsign=false",
            "-c", "core.sshCommand=ssh"
        };
        foreach (var key in filters.StandardOutput.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!FilterConfigKey.IsMatch(key))
                throw new InvalidOperationException("Repository contains an unsupported Git filter configuration; operation refused.");
            safeArguments.Add("-c");
            safeArguments.Add(key + "=");
        }
        safeArguments.AddRange(arguments);
        var result = await _runner.RunAsync("git", safeArguments, _files.Root, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
            throw new TimeoutException("Git did not finish within the allowed time.");
        if (allowedExitCodes?.Contains(result.ExitCode) != true && result.ExitCode != 0)
            throw new InvalidOperationException($"Git failed with exit code {result.ExitCode}: {RedactUrlCredentials(result.StandardError.Trim())}");
        return new WorkspaceProcessResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }

    private static string RedactUrlCredentials(string value) => UrlCredentials.Replace(value, "$1[redacted]@");

    private sealed record WorkspaceProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
