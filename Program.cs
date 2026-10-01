using System.Diagnostics;

Console.WriteLine("ShoppyAI Agent");
Console.WriteLine("Type 'help' for commands or 'exit' to quit.");

while (true)
{
    Console.Write("\n> ");
    var command = Console.ReadLine()?.Trim().ToLower();

    if (command == "exit")
        break;

    switch (command)
    {
        case "help":
            Console.WriteLine("Available commands:");
            Console.WriteLine("  help       Show commands");
            Console.WriteLine("  git status Show Git status");
            Console.WriteLine("  exit       Close agent");
            break;

        case "git status":
            RunCommand("git", "status");
            break;

        default:
            Console.WriteLine("Unknown command. Type 'help'.");
            break;
    }
}

static void RunCommand(string fileName, string arguments)
{
    try
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

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