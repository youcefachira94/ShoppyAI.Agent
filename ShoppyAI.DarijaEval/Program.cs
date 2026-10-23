using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShoppyAI.Core.Chat;
using ShoppyAI.Core.Language;
using ShoppyAI.Core.Ollama;
using ShoppyAI.DarijaEval;

// Runs the Darija evaluation set against a real Ollama model through ShoppyAI's own
// ChatSession (same system prompt, glossary, corrections and history handling as the app).
//
//   dotnet run --project ShoppyAI.DarijaEval -- [options]
//     --url <http://localhost:11434>   Ollama address
//     --model <qwen2.5-coder:14b>      model to test (never pulled or changed by this tool)
//     --out <dir>                      output folder (default: eval-results/<timestamp>)
//     --only <id,id>                   run only these cases
//     --category <name>                run only one category
//     --num-ctx <8192>  --temperature <0.5>  --seed <42>  --num-predict <n>
//     --dry-run                        no model call: show detection and prompt sizes only
//     --show-prompt                    with --dry-run, print each full system prompt

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = Encoding.UTF8;

var arguments = ParseArguments(args);
if (arguments is null)
    return 1;

var options = new OllamaOptions
{
    BaseUrl = arguments.GetValueOrDefault("url", OllamaOptions.DefaultBaseUrl),
    Model = arguments.GetValueOrDefault("model", OllamaOptions.DefaultModel),
    ContextLength = int.Parse(arguments.GetValueOrDefault("num-ctx", "8192")),
    Temperature = double.Parse(arguments.GetValueOrDefault("temperature", "0.5")),
    Seed = int.Parse(arguments.GetValueOrDefault("seed", "42")),
    MaxAnswerTokens = arguments.TryGetValue("num-predict", out var numPredict) ? int.Parse(numPredict) : null
};
var dryRun = arguments.ContainsKey("dry-run");
var outputDirectory = arguments.GetValueOrDefault("out", Path.Combine("eval-results", DateTime.Now.ToString("yyyyMMdd-HHmmss")));

var dataset = EvalDataset.Load(Path.Combine(AppContext.BaseDirectory, "darija-eval-dataset.json"));
var cases = dataset.Cases
    .Where(c => !arguments.TryGetValue("only", out var only) || only.Split(',').Contains(c.Id))
    .Where(c => !arguments.TryGetValue("category", out var category) || c.Category == category)
    .ToList();

// The dry run answers every turn with a fixed text instead of a model, so conversations,
// requests and saved preferences go through exactly the same steps as a real run.
using var http = dryRun ? new HttpClient(new CannedAnswerHandler()) : OllamaChatClient.CreateHttpClient();
http.Timeout = TimeSpan.FromMinutes(30);

if (!dryRun && await CheckModel(http, options) is { } problem)
{
    Console.Error.WriteLine(problem);
    return 2;
}

var resources = LanguageResources.Load();
var detector = new LanguageDetector(resources.Markers);
var client = new OllamaChatClient(http, options);
var results = new List<TurnResult>();
var totalTurns = cases.Sum(c => c.Turns.Count);
var started = Stopwatch.StartNew();

Console.WriteLine($"{(dryRun ? "Dry run" : $"Model {options.Model} at {options.BaseUrl}")}: {cases.Count} cases, {totalTurns} turns.");

foreach (var evalCase in cases)
{
    // Every case starts from an empty conversation and an empty, in-memory correction list.
    var corrections = new CorrectionStore(null);
    var preferences = new LanguagePreferences(store: null);
    preferences.Apply(evalCase.SavedPreference);
    var session = new ChatSession(client, resources, corrections, preferences);

    for (var turnIndex = 0; turnIndex < evalCase.Turns.Count; turnIndex++)
    {
        var turn = evalCase.Turns[turnIndex];
        if (turn.NewConversation)
            session.Reset();

        foreach (var correction in turn.CorrectionsBefore ?? [])
            corrections.AddOrUpdate(correction.Term, correction.Meaning, correction.Note);

        Console.Write($"[{results.Count + 1}/{totalTurns}] {evalCase.Id} #{turnIndex + 1} ");
        var watch = Stopwatch.StartNew();
        TurnResult result;

        if (dryRun)
        {
            if (arguments.ContainsKey("show-prompt"))
                Console.WriteLine($"\n----- system prompt -----\n{session.Prepare(turn.User).SystemPrompt}\n-------------------------");

            var prepared = (await session.ReplyAsync(turn.User)).Request;
            result = new TurnResult(evalCase, turnIndex, turn, prepared.Detection, prepared.GlossaryMatches.Select(g => g.Term).ToList(),
                prepared.SystemPrompt.Length, "", null, EvalChecks.Decision(turn.Expect, prepared.Reply), null, TimeSpan.Zero, prepared.Reply);
        }
        else
        {
            try
            {
                var reply = await session.ReplyAsync(turn.User);
                var replyDetection = detector.Detect(reply.Content);
                var checks = EvalChecks.Run(turn.Expect, reply.Content, replyDetection);
                checks.InsertRange(0, EvalChecks.Decision(turn.Expect, reply.Request.Reply));
                result = new TurnResult(evalCase, turnIndex, turn, reply.Request.Detection,
                    reply.Request.GlossaryMatches.Select(g => g.Term).ToList(), reply.Request.SystemPrompt.Length,
                    reply.Content, replyDetection, checks, reply.Metrics, watch.Elapsed, reply.Request.Reply);
            }
            catch (Exception error) when (error is OllamaException or HttpRequestException or IOException or TaskCanceledException)
            {
                result = new TurnResult(evalCase, turnIndex, turn, detector.Detect(turn.User), [], 0, "", null,
                    [new CheckResult("request", false, error.Message)], null, watch.Elapsed);
            }
        }

        results.Add(result);
        Console.WriteLine(dryRun
            ? $"{result.InputDetection.Language}/{result.InputDetection.Script} -> reply {result.Decision?.Describe()} ({result.Decision?.Source}) " +
              $"{string.Concat(result.Checks.Select(c => c.Passed ? "+" : "-"))}, glossary: {string.Join(", ", result.Glossary)}, prompt {result.SystemPromptChars} chars"
            : $"{watch.Elapsed.TotalSeconds:0.0}s {string.Concat(result.Checks.Select(c => c.Passed ? "+" : "-"))}");
    }
}

Directory.CreateDirectory(outputDirectory);
var jsonPath = Path.Combine(outputDirectory, "results.json");
var reportPath = Path.Combine(outputDirectory, "report.md");
await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(results.Select(r => r.ToJson()), EvalDataset.JsonOptions));
await File.WriteAllTextAsync(reportPath, Report.Build(results, options, dryRun, started.Elapsed));

Console.WriteLine($"\nWrote {reportPath} and {jsonPath}");
return 0;

static Dictionary<string, string>? ParseArguments(string[] args)
{
    var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--"))
        {
            Console.Error.WriteLine($"Unexpected argument: {args[i]}");
            return null;
        }

        var name = args[i][2..];
        var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
        parsed[name] = hasValue ? args[++i] : "true";
    }

    return parsed;
}

static async Task<string?> CheckModel(HttpClient http, OllamaOptions options)
{
    try
    {
        var tags = JsonNode.Parse(await http.GetStringAsync(new Uri(new Uri(options.BaseUrl.TrimEnd('/') + "/"), "api/tags")));
        var models = tags?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>()).ToList() ?? [];
        return models.Contains(options.Model)
            ? null
            : $"Model {options.Model} is not installed in Ollama. Installed: {string.Join(", ", models)}. Install it with: ollama pull {options.Model}";
    }
    catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
    {
        return $"Cannot reach Ollama at {options.BaseUrl}: {error.Message}";
    }
}

public sealed record TurnResult(
    EvalCase Case,
    int TurnIndex,
    EvalTurn Turn,
    LanguageDetection InputDetection,
    IReadOnlyList<string> Glossary,
    int SystemPromptChars,
    string Reply,
    LanguageDetection? ReplyDetection,
    IReadOnlyList<CheckResult> Checks,
    OllamaMetrics? Metrics,
    TimeSpan Elapsed,
    ReplyDecision? Decision = null)
{
    public object ToJson() => new
    {
        id = Case.Id,
        category = Case.Category,
        turn = TurnIndex + 1,
        user = Turn.User,
        correctionsBefore = Turn.CorrectionsBefore,
        preference = Case.Preference,
        inputDetection = InputDetection.Describe(),
        replyAskedFor = Decision?.Describe(),
        replyReason = Decision?.Source.ToString(),
        glossary = Glossary,
        systemPromptChars = SystemPromptChars,
        reply = Reply,
        replyDetection = ReplyDetection?.Describe(),
        checks = Checks,
        promptTokens = Metrics?.PromptTokens,
        answerTokens = Metrics?.CompletionTokens,
        seconds = Math.Round(Elapsed.TotalSeconds, 1)
    };
}

/// <summary>Stands in for Ollama in --dry-run: every answer is the same short text.</summary>
internal sealed class CannedAnswerHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"message\":{\"role\":\"assistant\",\"content\":\"(dry run)\"},\"done\":false}\n" +
                "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true}\n")
        });
}
