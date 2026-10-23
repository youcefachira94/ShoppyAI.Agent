using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShoppyAI.Core.Language;

namespace ShoppyAI.Tests;

internal static class TestData
{
    private static readonly Lazy<LanguageResources> LazyResources = new(() => LanguageResources.Load());

    public static LanguageResources Resources => LazyResources.Value;

    public static LanguageDetector Detector { get; } = new(Resources.Markers);

    public static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "shoppyai-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>Stands in for Ollama: records each /api/chat request and answers with NDJSON chunks.</summary>
internal sealed class FakeOllamaHandler : HttpMessageHandler
{
    private readonly Func<JsonObject, HttpResponseMessage> _respond;

    public FakeOllamaHandler(Func<JsonObject, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    public List<(Uri Uri, JsonObject Body)> Requests { get; } = [];

    public static FakeOllamaHandler Answering(params string[] chunks) => new(_ => Stream(chunks));

    public static HttpResponseMessage Stream(params string[] chunks)
    {
        var lines = new StringBuilder();
        foreach (var chunk in chunks)
            lines.AppendLine(JsonSerializer.Serialize(new { message = new { role = "assistant", content = chunk }, done = false }));

        lines.AppendLine("""{"message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","prompt_eval_count":120,"eval_count":12,"total_duration":1500000000}""");

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(lines.ToString(), Encoding.UTF8, "application/x-ndjson")
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        Requests.Add((request.RequestUri!, body));
        return _respond(body);
    }

    public static IReadOnlyList<(string Role, string Content)> Messages(JsonObject body) =>
        body["messages"]!.AsArray()
            .Select(m => (m!["role"]!.GetValue<string>(), m["content"]!.GetValue<string>()))
            .ToList();
}
