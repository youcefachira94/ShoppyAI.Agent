namespace ShoppyAI.Core.Workspace;

/// <summary>Read-only file access confined to one explicitly selected workspace.</summary>
public sealed class WorkspaceFileService
{
    private static readonly HashSet<string> SensitiveDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".ssh", ".aws", ".azure", ".gnupg", ".gpg", ".secrets", "secrets", "credentials", ".docker", ".terraform"
    };

    private static readonly HashSet<string> SensitiveFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".netrc", ".npmrc", ".pypirc", "credentials", "id_rsa", "id_ed25519", "id_ecdsa", "id_dsa", "terraform.tfstate"
    };

    private static readonly HashSet<string> SensitiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pem", ".key", ".pfx", ".p12", ".p8", ".keystore", ".jks"
    };

    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public WorkspaceFileService(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        var fullRoot = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException($"Workspace does not exist: {fullRoot}");

        var rootInfo = new DirectoryInfo(fullRoot);
        _root = Path.GetFullPath(rootInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? rootInfo.FullName);
        _rootPrefix = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
    }

    public string Root => _root;

    /// <summary>Reads a relative workspace file after validating every existing path component.</summary>
    public string ReadText(string relativePath)
    {
        var path = ResolveExistingFile(relativePath);

        return File.ReadAllText(path);
    }

    /// <summary>Validates a file path for an operation such as explicit Git staging.</summary>
    public string ValidateExistingFile(string relativePath) => ResolveExistingFile(relativePath);

    /// <summary>Validates a relative path even when a Git operation targets a removed file.</summary>
    public string ValidateRelativePath(string relativePath)
    {
        var path = ResolveWorkspacePath(relativePath);
        RejectReparsePoints(path);
        return Path.GetRelativePath(_root, path);
    }

    /// <summary>Lists eligible files recursively, without entering links or sensitive directories.</summary>
    public IReadOnlyList<string> ListFiles()
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(_root);

        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                var name = Path.GetFileName(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!SensitiveDirectories.Contains(name))
                        pending.Push(entry);
                    continue;
                }

                if (!IsSensitiveFileName(name))
                    result.Add(Path.GetRelativePath(_root, entry));
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private string ResolveWorkspacePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || IsRootedPath(relativePath))
            throw new WorkspaceAccessException("Only relative paths inside the selected workspace are allowed.");

        try
        {
            if (OperatingSystem.IsWindows() && relativePath.Contains(':'))
                throw new WorkspaceAccessException("Alternate data streams and device paths are not allowed.");

            var portableRelativePath = relativePath
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(_root, portableRelativePath));
            if (!fullPath.StartsWith(_rootPrefix, _pathComparison) &&
                !string.Equals(fullPath, _root, _pathComparison))
            {
                throw new WorkspaceAccessException("The requested path escapes the selected workspace.");
            }

            RejectSensitivePath(fullPath);
            return fullPath;
        }
        catch (WorkspaceAccessException)
        {
            throw;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new WorkspaceAccessException("The requested path is invalid.", error);
        }
    }

    private string ResolveExistingFile(string relativePath)
    {
        var path = ResolveWorkspacePath(relativePath);
        RejectReparsePoints(path);
        if (Directory.Exists(path))
            throw new WorkspaceAccessException("The requested path is a directory; only files are accessible.");
        if (!File.Exists(path))
            throw new FileNotFoundException("Workspace file was not found.", relativePath);
        return path;
    }

    private void RejectReparsePoints(string fullPath)
    {
        var relative = Path.GetRelativePath(_root, fullPath);
        var current = _root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new WorkspaceAccessException("Symbolic links and junctions are not accessible from the workspace.");
        }
    }

    private void RejectSensitivePath(string fullPath)
    {
        var relative = Path.GetRelativePath(_root, fullPath);
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < segments.Length; i++)
        {
            if (i < segments.Length - 1 && SensitiveDirectories.Contains(segments[i]))
                throw new WorkspaceAccessException("The requested path is in a sensitive directory.");
        }

        if (segments.Length > 0 && IsSensitiveFileName(segments[^1]))
            throw new WorkspaceAccessException("The requested file is excluded by the workspace security policy.");
    }

    private static bool IsSensitiveFileName(string name)
    {
        if (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
            SensitiveFileNames.Contains(name) ||
            name.Equals("secrets", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("secrets.", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("credentials.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return SensitiveExtensions.Contains(Path.GetExtension(name));
    }

    private static bool IsRootedPath(string path) =>
        Path.IsPathRooted(path) ||
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '/' || path[2] == '\\')) ||
        path.StartsWith("\\\\", StringComparison.Ordinal);
}
