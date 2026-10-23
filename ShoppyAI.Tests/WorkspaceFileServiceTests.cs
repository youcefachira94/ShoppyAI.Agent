using ShoppyAI.Core.Workspace;

namespace ShoppyAI.Tests;

public sealed class WorkspaceFileServiceTests : IDisposable
{
    private readonly string _workspace = TestData.NewTempDirectory();

    [Fact]
    public void Reads_files_inside_the_selected_workspace()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "src"));
        File.WriteAllText(Path.Combine(_workspace, "src", "app.cs"), "safe");

        var service = new WorkspaceFileService(_workspace);

        Assert.Equal("safe", service.ReadText(Path.Combine("src", "app.cs")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("\\\\server\\share\\secret.txt")]
    public void Rejects_paths_outside_the_workspace(string path)
    {
        var service = new WorkspaceFileService(_workspace);

        Assert.Throws<WorkspaceAccessException>(() => service.ReadText(path));
    }

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData("id_rsa")]
    [InlineData("service.pfx")]
    [InlineData("secrets.json")]
    public void Refuses_sensitive_files(string fileName)
    {
        File.WriteAllText(Path.Combine(_workspace, fileName), "private");

        var service = new WorkspaceFileService(_workspace);

        Assert.Throws<WorkspaceAccessException>(() => service.ReadText(fileName));
        Assert.DoesNotContain(service.ListFiles(), path => Path.GetFileName(path) == fileName);
    }

    [Fact]
    public void Refuses_symlinked_files_and_does_not_follow_directory_links()
    {
        var outside = TestData.NewTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside");
            File.CreateSymbolicLink(Path.Combine(_workspace, "linked.txt"), Path.Combine(outside, "secret.txt"));
            Directory.CreateSymbolicLink(Path.Combine(_workspace, "linked-dir"), outside);
        }
        catch (Exception error) when (error is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            Directory.Delete(outside, recursive: true);
            return;
        }

        try
        {
            var service = new WorkspaceFileService(_workspace);
            Assert.Throws<WorkspaceAccessException>(() => service.ReadText("linked.txt"));
            Assert.DoesNotContain(service.ListFiles(), path => path.Contains("linked-dir", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Does_not_list_or_read_sensitive_directories()
    {
        var credentials = Path.Combine(_workspace, ".aws");
        Directory.CreateDirectory(credentials);
        File.WriteAllText(Path.Combine(credentials, "credentials"), "private");
        var service = new WorkspaceFileService(_workspace);

        Assert.Throws<WorkspaceAccessException>(() => service.ReadText(Path.Combine(".aws", "credentials")));
        Assert.DoesNotContain(service.ListFiles(), path => path.Contains(".aws", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Blocks_credentials_and_secrets_directories()
    {
        foreach (var directory in new[] { "secrets", "credentials" })
        {
            var path = Path.Combine(_workspace, directory);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "token.txt"), "private");
        }

        var service = new WorkspaceFileService(_workspace);

        Assert.Throws<WorkspaceAccessException>(() => service.ReadText(Path.Combine("secrets", "token.txt")));
        Assert.Throws<WorkspaceAccessException>(() => service.ReadText(Path.Combine("credentials", "token.txt")));
        Assert.DoesNotContain(service.ListFiles(), path => path.Contains("secrets", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(service.ListFiles(), path => path.Contains("credentials", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose() => Directory.Delete(_workspace, recursive: true);
}
