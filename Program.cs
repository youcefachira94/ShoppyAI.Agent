using System.Text;
using ShoppyAI.Core.Chat;
using ShoppyAI.Core.Git;
using ShoppyAI.Core.Language;
using ShoppyAI.Core.Ollama;
using ShoppyAI.Core.Settings;
using ShoppyAI.Core.Workspace;

// Arabic needs Unicode in the console. On a Windows console, UTF-16 makes .NET use the
// wide console API (ReadConsoleW/WriteConsoleW), which also keeps French accents working;
// redirected streams and other systems use UTF-8.
try
{
    if (OperatingSystem.IsWindows() && !Console.IsInputRedirected)
        Console.InputEncoding = Encoding.Unicode;

    Console.OutputEncoding = OperatingSystem.IsWindows() && !Console.IsOutputRedirected
        ? Encoding.Unicode
        : Encoding.UTF8;
}
catch (IOException)
{
    // No console attached: keep the defaults.
}

var settings = new SettingsStore();
var workspace = new WorkspaceFileService(GetWorkspacePath(args));
var git = new GitWorkspaceService(workspace.Root);
var gitApproval = new ConsoleGitActionApproval();
var corrections = new CorrectionStore(ShoppyPaths.CorrectionsFile);
var languageResources = LanguageResources.Load(ShoppyPaths.LanguageDirectory);
var session = new ChatSession(
    new OllamaChatClient(OllamaChatClient.CreateHttpClient(), settings.EffectiveOllamaOptions()),
    languageResources,
    corrections,
    () => settings.Settings.ResponseLanguage);
var languageCommands = new LanguageCommands(corrections, () => settings.Settings.ResponseLanguage, language =>
{
    settings.Settings.ResponseLanguage = language;
    try
    {
        settings.Save();
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        Console.WriteLine($"Warning: the language choice could not be saved ({error.Message}).");
    }
});

Console.WriteLine("ShoppyAI Agent");
Console.WriteLine($"Workspace: {workspace.Root}");
Console.WriteLine("Type 'help' for commands or 'exit' to quit.");

foreach (var problem in new[] { settings.LoadError, corrections.LoadError, languageResources.UserGlossaryError })
{
    if (problem is not null)
        Console.WriteLine($"Warning: {problem}");
}

while (true)
{
    Console.Write("\n> ");
    var input = Console.ReadLine()?.Trim();

    if (string.IsNullOrWhiteSpace(input))
        continue;

    if (input.Equals("exit", StringComparison.OrdinalIgnoreCase))
        break;

    if (languageCommands.TryExecute(input) is { } languageResult)
    {
        Console.WriteLine(languageResult);
        continue;
    }

    var command = input.ToLower();

    switch (command)
    {
        case "help":
            ShowHelp();
            break;

        case "git status":
            await RunGitReadAsync(git.StatusAsync);
            break;

        case "git diff":
            await RunGitReadAsync(git.DiffAsync);
            break;

        case "git log":
            await RunGitReadAsync(git.LogAsync);
            break;

        case "git stage":
            Console.Write("File paths to stage (separate with ;): ");
            var selectedPaths = (Console.ReadLine() ?? "")
                .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            try
            {
                await git.StageFilesAsync(selectedPaths);
                Console.WriteLine($"Staged {selectedPaths.Length} explicitly selected file(s).");
            }
            catch (Exception error)
            {
                Console.WriteLine($"Git staging refused: {error.Message}");
            }
            break;

        case "git commit":
            Console.Write("Commit message: ");
            var message = Console.ReadLine();

            if (!string.IsNullOrWhiteSpace(message))
            {
                try
                {
                    var committed = await git.CommitAsync(message, gitApproval);
                    Console.WriteLine(committed ? "Commit created." : "Commit cancelled.");
                }
                catch (Exception error)
                {
                    Console.WriteLine($"Git commit refused: {error.Message}");
                }
            }
            else
            {
                Console.WriteLine("Commit cancelled: message is empty.");
            }

            break;

        case "git push":
            try
            {
                var pushed = await git.PushAsync(gitApproval);
                Console.WriteLine(pushed ? "Push completed." : "Push cancelled.");
            }
            catch (Exception error)
            {
                Console.WriteLine($"Git push refused: {error.Message}");
            }
            break;

        case "scan":
        case "files":
            ScanProject(workspace);
            break;
case "ask":
    Console.Write("Your question: ");
    var question = Console.ReadLine();

    if (!string.IsNullOrWhiteSpace(question))
        await AskOllama(session, question);

    break;

        case "new":
            session.Reset();
            Console.WriteLine("New conversation started.");
            break;


        default:
            if (input.StartsWith("read ", StringComparison.OrdinalIgnoreCase))
            {
                var fileName = input[5..].Trim();
                try
                {
                    Console.WriteLine($"\n--- {fileName} ---");
                    Console.WriteLine(workspace.ReadText(fileName));
                    Console.WriteLine("--- End ---");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    Console.WriteLine($"File read refused: {error.Message}");
                }
            }
            else
            {
                Console.WriteLine("Unknown command. Type 'help'.");
            }

            break;
    }
}

static void ShowHelp()
{
    Console.WriteLine("Available commands:");
    Console.WriteLine("  help");
    Console.WriteLine("  scan");
    Console.WriteLine("  files");
    Console.WriteLine("  read <file>");
    Console.WriteLine("  ask            (the conversation is remembered until 'new')");
    Console.WriteLine("  new            start a new conversation");
    Console.WriteLine("  git status");
    Console.WriteLine("  git diff");
    Console.WriteLine("  git log");
    Console.WriteLine("  git stage     (explicit file paths separated by ;)");
    Console.WriteLine("  git commit");
    Console.WriteLine("  git push");
    Console.WriteLine("  exit");
    Console.WriteLine();
    Console.WriteLine("Language:");
    foreach (var line in LanguageCommands.Help.Split('\n'))
        Console.WriteLine($"  {line}");
}

static string GetWorkspacePath(string[] arguments)
{
    var index = Array.IndexOf(arguments, "--workspace");
    if (index < 0)
        return Directory.GetCurrentDirectory();
    if (index + 1 >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index + 1]))
        throw new ArgumentException("Pass a directory after --workspace.");
    return Path.GetFullPath(arguments[index + 1]);
}

static void ScanProject(WorkspaceFileService workspace)
{
    var files = workspace.ListFiles();
    Console.WriteLine($"Workspace files found: {files.Count}");
    foreach (var file in files)
        Console.WriteLine(file);
}

static async Task RunGitReadAsync(Func<CancellationToken, Task<string>> operation)
{
    try
    {
        Console.WriteLine(await operation(CancellationToken.None));
    }
    catch (Exception error)
    {
        Console.WriteLine($"Git command failed: {error.Message}");
    }
}

static async Task AskOllama(ChatSession session, string question)
{
    try
    {
        await foreach (var delta in session.StreamReplyAsync(question))
            Console.Write(delta);

        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ollama error: {ex.Message}");
    }
}

sealed class ConsoleGitActionApproval : IGitActionApproval
{
    public Task<bool> ApproveCommitAsync(string branch, string message, string stagedDiff, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"Commit preview — branch: {branch}; message: {message}");
        Console.WriteLine(stagedDiff);
        Console.Write("Type 'yes' to create this commit: ");
        return Task.FromResult(string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase));
    }

    public Task<bool> ApprovePushAsync(string remote, string destination, string branch, IReadOnlyList<string> commits, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"Push preview — remote: {remote}; destination: {destination}; branch: {branch}");
        foreach (var commit in commits)
            Console.WriteLine($"  {commit}");
        Console.Write("Type 'yes' to push these commits: ");
        return Task.FromResult(string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase));
    }
}
