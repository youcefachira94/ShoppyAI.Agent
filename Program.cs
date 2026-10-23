using System.Diagnostics;
using System.Text;
using ShoppyAI.Core.Chat;
using ShoppyAI.Core.Language;
using ShoppyAI.Core.Ollama;
using ShoppyAI.Core.Settings;

try
{
    Console.InputEncoding = Encoding.UTF8;
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // No console attached (redirected input/output): keep the defaults.
}

var settings = new SettingsStore();
var corrections = new CorrectionStore(ShoppyPaths.CorrectionsFile);
var session = new ChatSession(
    new OllamaChatClient(OllamaChatClient.CreateHttpClient(), settings.EffectiveOllamaOptions()),
    LanguageResources.Load(ShoppyPaths.LanguageDirectory),
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
Console.WriteLine("Type 'help' for commands or 'exit' to quit.");

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
            RunCommand("git", "status");
            break;

        case "git diff":
            RunCommand("git", "diff");
            break;

        case "git log":
            RunCommand("git", "log", "--oneline", "-10");
            break;

        case "git commit":
            Console.Write("Commit message: ");
            var message = Console.ReadLine();

            if (!string.IsNullOrWhiteSpace(message))
            {
                RunCommand("git", "add", ".");
                RunCommand("git", "commit", "-m", message);
            }
            else
            {
                Console.WriteLine("Commit cancelled: message is empty.");
            }

            break;

        case "git push":
            RunCommand("git", "push");
            break;

        case "scan":
        case "files":
            ScanProject();
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
                ReadProjectFile(fileName);
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
    Console.WriteLine("  git commit");
    Console.WriteLine("  git push");
    Console.WriteLine("  exit");
    Console.WriteLine();
    Console.WriteLine("Language:");
    foreach (var line in LanguageCommands.Help.Split('\n'))
        Console.WriteLine($"  {line}");
}

static void ScanProject()
{
    var root = Directory.GetCurrentDirectory();

    var files = Directory
        .GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(file =>
            !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase) &&
            !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase) &&
            !file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
        .ToList();

    Console.WriteLine($"Project files found: {files.Count}");

    foreach (var file in files)
    {
        Console.WriteLine(Path.GetRelativePath(root, file));
    }
}

static void ReadProjectFile(string fileName)
{
    var fullPath = Path.GetFullPath(fileName);

    if (!File.Exists(fullPath))
    {
        Console.WriteLine($"File not found: {fileName}");
        return;
    }

    Console.WriteLine($"\n--- {fileName} ---");
    Console.WriteLine(File.ReadAllText(fullPath));
    Console.WriteLine("--- End ---");
}

static void RunCommand(string fileName, params string[] arguments)
{
    try
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();

        Console.WriteLine(process.StandardOutput.ReadToEnd());
        Console.WriteLine(process.StandardError.ReadToEnd());

        process.WaitForExit();
    }
        catch (Exception error)
    {
        Console.WriteLine($"Error: {error.Message}");
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
