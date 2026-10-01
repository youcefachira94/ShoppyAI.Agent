using System.Diagnostics;

Console.WriteLine("ShoppyAI Agent");
Console.WriteLine("Type 'help' for commands or 'exit' to quit.");

while (true)
{
    Console.Write("\n> ");
    var command = Console.ReadLine()?.Trim();

    if (string.IsNullOrWhiteSpace(command))
        continue;

    if (command.Equals("exit", StringComparison.OrdinalIgnoreCase))
        break;

    switch (command.ToLower())
    {
        case "help":
            Console.WriteLine("help");
            Console.WriteLine("git status");
            Console.WriteLine("git diff");
            Console.WriteLine("git log");
            Console.WriteLine("git commit");
            Console.WriteLine("git push");
            Console.WriteLine("exit");
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
                RunCommand("git", "add", ".");
                RunCommand("git", "commit", "-m", message);
            break;

        case "git push":
            RunCommand("git", "push");
            break;

        default:
            Console.WriteLine("Unknown command. Type 'help'.");
            break;
    }
}

static void RunCommand(string fileName, params string[] arguments)
{
    try
    {
        var process = new Process
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
            process.StartInfo.ArgumentList.Add(argument);

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