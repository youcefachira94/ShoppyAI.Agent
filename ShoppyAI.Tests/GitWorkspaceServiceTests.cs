using ShoppyAI.Core.Git;
using ShoppyAI.Core.Workspace;

namespace ShoppyAI.Tests;

public sealed class GitWorkspaceServiceTests : IDisposable
{
    private readonly string _workspace = TestData.NewTempDirectory();

    public GitWorkspaceServiceTests()
    {
        Git("init", "--initial-branch=feature/security");
        Git("config", "user.name", "ShoppyAI Tests");
        Git("config", "user.email", "tests@example.invalid");
    }

    [Fact]
    public async Task Stages_only_explicitly_selected_workspace_files()
    {
        File.WriteAllText(Path.Combine(_workspace, "safe.cs"), "class Safe {}\n");
        File.WriteAllText(Path.Combine(_workspace, "other.cs"), "class Other {}\n");
        var service = new GitWorkspaceService(_workspace);

        await service.StageFilesAsync(["safe.cs"]);

        var staged = Git("diff", "--cached", "--name-only");
        Assert.Equal("safe.cs", staged.Trim());
    }

    [Theory]
    [InlineData(".")]
    [InlineData("../outside.cs")]
    [InlineData(".env")]
    public async Task Refuses_broad_traversal_and_sensitive_staging(string path)
    {
        File.WriteAllText(Path.Combine(_workspace, ".env"), "secret");
        var service = new GitWorkspaceService(_workspace);

        await Assert.ThrowsAsync<WorkspaceAccessException>(() => service.StageFilesAsync([path]));
        Assert.Empty(Git("diff", "--cached", "--name-only").Trim());
    }

    [Fact]
    public async Task Commit_requires_separate_approval_and_reviews_the_staged_diff()
    {
        File.WriteAllText(Path.Combine(_workspace, "safe.cs"), "class Safe {}\n");
        var service = new GitWorkspaceService(_workspace);
        await service.StageFilesAsync(["safe.cs"]);
        var approval = new FakeGitApproval(approveCommit: false);

        var committed = await service.CommitAsync("Add safe file", approval);

        Assert.False(committed);
        Assert.Contains("class Safe", approval.CommitDiff);
        Assert.Equal("A  safe.cs", Git("status", "--short").Trim());
    }

    [Fact]
    public async Task Commit_refuses_main_and_master_even_if_approved()
    {
        Git("branch", "-M", "main");
        File.WriteAllText(Path.Combine(_workspace, "safe.cs"), "class Safe {}\n");
        var service = new GitWorkspaceService(_workspace);
        await service.StageFilesAsync(["safe.cs"]);
        var approval = new FakeGitApproval(approveCommit: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CommitAsync("Unsafe branch commit", approval));
        Assert.Equal(0, approval.CommitApprovals);
    }

    [Fact]
    public async Task Commit_blocks_secret_patterns_before_asking_for_approval()
    {
        File.WriteAllText(Path.Combine(_workspace, "config.cs"), "const string api_key = \"supersecretvalue123\";\n");
        var service = new GitWorkspaceService(_workspace);
        await service.StageFilesAsync(["config.cs"]);
        var approval = new FakeGitApproval(approveCommit: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CommitAsync("Add config", approval));

        Assert.Equal(0, approval.CommitApprovals);
    }

    [Fact]
    public async Task Commit_blocks_binary_changes_that_cannot_be_secret_scanned()
    {
        File.WriteAllBytes(Path.Combine(_workspace, "opaque.bin"), [0, 1, 2, 3, 0, 255]);
        var service = new GitWorkspaceService(_workspace);
        await service.StageFilesAsync(["opaque.bin"]);
        var approval = new FakeGitApproval(approveCommit: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CommitAsync("Add binary", approval));

        Assert.Equal(0, approval.CommitApprovals);
    }

    [Fact]
    public async Task Commit_does_not_execute_repository_hooks()
    {
        if (OperatingSystem.IsWindows())
            return;

        var sentinel = Path.Combine(_workspace, "hook-ran.txt");
        var hooks = Path.Combine(_workspace, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        var hook = Path.Combine(hooks, "pre-commit");
        File.WriteAllText(hook, $"#!/bin/sh\nprintf executed > '{sentinel}'\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(_workspace, "safe.cs"), "class Safe {}\n");
        var service = new GitWorkspaceService(_workspace);
        await service.StageFilesAsync(["safe.cs"]);

        Assert.True(await service.CommitAsync("Commit with hook present", new FakeGitApproval()));

        Assert.False(File.Exists(sentinel));
    }

    [Fact]
    public async Task Staging_does_not_execute_repository_clean_filters()
    {
        var sentinel = Path.Combine(_workspace, "filter-ran.txt");
        File.WriteAllText(Path.Combine(_workspace, ".gitattributes"), "*.txt filter=evil\n");
        File.WriteAllText(Path.Combine(_workspace, "safe.txt"), "safe content\n");
        Git("config", "filter.evil.clean", $"touch {sentinel}");
        var service = new GitWorkspaceService(_workspace);

        await service.StageFilesAsync(["safe.txt"]);

        Assert.False(File.Exists(sentinel));
        Assert.Equal("safe.txt", Git("diff", "--cached", "--name-only").Trim());
    }

    [Fact]
    public async Task Push_requires_its_own_approval_and_shows_the_exact_upstream()
    {
        var remote = TestData.NewTempDirectory();
        Git("init", "--bare", remote);
        try
        {
            await CreateRemoteTrackingBranch(remote);
            Git("remote", "set-url", "origin", "https://user:secret@example.invalid/org/repo.git");
            var service = new GitWorkspaceService(_workspace);
            File.WriteAllText(Path.Combine(_workspace, "next.cs"), "class Next {}\n");
            await service.StageFilesAsync(["next.cs"]);
            await service.CommitAsync("Add next file", new FakeGitApproval());
            var approval = new FakeGitApproval(approvePush: false);

            var pushed = await service.PushAsync(approval);

            Assert.False(pushed);
            Assert.Equal(1, approval.PushApprovals);
            Assert.Equal("origin", approval.PushRemote);
            Assert.Equal("feature/security", approval.PushBranch);
            Assert.Equal("https://example.invalid/org/repo.git", approval.PushDestination);
            Assert.Single(approval.PushCommits);
            Assert.NotEqual(Git("rev-parse", "HEAD").Trim(), GitRemote(remote, "rev-parse", "refs/heads/feature/security").Trim());
        }
        finally
        {
            Directory.Delete(remote, recursive: true);
        }
    }

    [Fact]
    public async Task Push_refuses_custom_remote_helpers_before_approval()
    {
        var remote = TestData.NewTempDirectory();
        Git("init", "--bare", remote);
        try
        {
            await CreateRemoteTrackingBranch(remote);
            Git("remote", "set-url", "origin", "ext::not-a-real-helper");
            var service = new GitWorkspaceService(_workspace);
            File.WriteAllText(Path.Combine(_workspace, "next.cs"), "class Next {}\n");
            await service.StageFilesAsync(["next.cs"]);
            await service.CommitAsync("Add next file", new FakeGitApproval());
            var approval = new FakeGitApproval(approvePush: true);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PushAsync(approval));

            Assert.Equal(0, approval.PushApprovals);
        }
        finally
        {
            Directory.Delete(remote, recursive: true);
        }
    }

    [Fact]
    public async Task Push_refuses_when_there_is_no_upstream()
    {
        var service = new GitWorkspaceService(_workspace);
        var approval = new FakeGitApproval(approvePush: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PushAsync(approval));
        Assert.Equal(0, approval.PushApprovals);
    }

    private string Git(params string[] args)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = _workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout;
    }

    private async Task CreateRemoteTrackingBranch(string remote)
    {
        File.WriteAllText(Path.Combine(_workspace, "base.cs"), "class Base {}\n");
        var service = new GitWorkspaceService(_workspace);
        await service.StageFilesAsync(["base.cs"]);
        Assert.True(await service.CommitAsync("Add base file", new FakeGitApproval()));
        Git("remote", "add", "origin", remote);
        Git("push", "--set-upstream", "origin", "HEAD");
    }

    private static string GitRemote(string remote, params string[] args)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = Path.GetTempPath(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add("--git-dir");
        process.StartInfo.ArgumentList.Add(remote);
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout;
    }

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    private sealed class FakeGitApproval(bool approveCommit = true, bool approvePush = true) : IGitActionApproval
    {
        public int CommitApprovals { get; private set; }
        public int PushApprovals { get; private set; }
        public string CommitDiff { get; private set; } = "";
        public string PushRemote { get; private set; } = "";
        public string PushDestination { get; private set; } = "";
        public string PushBranch { get; private set; } = "";
        public IReadOnlyList<string> PushCommits { get; private set; } = [];

        public Task<bool> ApproveCommitAsync(string branch, string message, string stagedDiff, CancellationToken cancellationToken = default)
        {
            CommitApprovals++;
            CommitDiff = stagedDiff;
            return Task.FromResult(approveCommit);
        }

        public Task<bool> ApprovePushAsync(string remote, string destination, string branch, IReadOnlyList<string> commits, CancellationToken cancellationToken = default)
        {
            PushApprovals++;
            PushRemote = remote;
            PushDestination = destination;
            PushBranch = branch;
            PushCommits = commits;
            return Task.FromResult(approvePush);
        }
    }
}
