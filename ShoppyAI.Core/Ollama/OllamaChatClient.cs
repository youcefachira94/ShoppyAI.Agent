using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShoppyAI.Core.Conversations;

namespace ShoppyAI.Core.Ollama;

public sealed class OllamaOptions
{
    public const string DefaultBaseUrl = "http://localhost:11434";
    public const string DefaultModel = "qwen2.5-coder:14b";

    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>Model name as listed by <c>ollama list</c>. ShoppyAI never changes it by itself.</summary>
    public string Model { get; set; } = DefaultModel;

    /// <summary>Sampling temperature; null keeps the model default.</summary>
    public double? Temperature { get; set; } = 0.5;

    /// <summary>Context window requested from Ollama (num_ctx), in tokens.</summary>
    public int ContextLength { get; set; } = 8192;

    /// <summary>Tokens kept free for the answer when trimming the conversation.</summary>
    public int ReservedAnswerTokens { get; set; } = 1024;

    /// <summary>Maximum answer length (num_predict); null lets the model finish.</summary>
    public int? MaxAnswerTokens { get; set; }

    /// <summary>Sampling seed for reproducible runs; null for random.</summary>
    public int? Seed { get; set; }
}

public sealed record OllamaMetrics(int PromptTokens, int CompletionTokens, TimeSpan TotalDuration, string? DoneReason);

public sealed record OllamaChatChunk(string Content, OllamaMetrics? Metrics = null)
{
    public bool Done => Metrics is not null;
}

public sealed record OllamaChatResult(string Content, OllamaMetrics? Metrics);

public sealed class OllamaException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Client for Ollama's /api/chat endpoint (system, user and assistant roles).</summary>
public sealed class OllamaChatClient
{
    private readonly HttpClient _http;

    public OllamaChatClient(HttpClient http, OllamaOptions options)
    {
        _http = http;
        Options = options;
    }

    public OllamaOptions Options { get; }

    public static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromMinutes(10) };

    public async IAsyncEnumerable<OllamaChatChunk> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // A buffered body (with Content-Length) works with every proxy, unlike a chunked one.
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("/api/chat"))
        {
            Content = new StringContent(BuildRequest(messages, stream: true).ToJsonString(), Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException error)
        {
            throw new OllamaException(
                $"Cannot reach Ollama at {Options.BaseUrl} ({error.Message}). Check that Ollama is running (ollama serve).", error);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw await ErrorFromResponse(response, cancellationToken);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var chunk = ParseChunk(line);
                yield return chunk;

                if (chunk.Done)
                    yield break;
            }
        }

        throw new OllamaException("Ollama closed the connection before the answer was complete.");
    }

    public async Task<OllamaChatResult> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var content = new StringBuilder();
        OllamaMetrics? metrics = null;

        await foreach (var chunk in StreamAsync(messages, cancellationToken))
        {
            content.Append(chunk.Content);
            metrics = chunk.Metrics ?? metrics;
        }

        return new OllamaChatResult(content.ToString(), metrics);
    }

    internal JsonObject BuildRequest(IReadOnlyList<ChatMessage> messages, bool stream)
    {
        var options = new JsonObject { ["num_ctx"] = Options.ContextLength };
        if (Options.Temperature is { } temperature)
            options["temperature"] = temperature;
        if (Options.MaxAnswerTokens is { } maxAnswerTokens)
            options["num_predict"] = maxAnswerTokens;
        if (Options.Seed is { } seed)
            options["seed"] = seed;

        return new JsonObject
        {
            ["model"] = Options.Model,
            ["messages"] = new JsonArray(messages
                .Select(m => (JsonNode)new JsonObject { ["role"] = m.RoleName, ["content"] = m.Content })
                .ToArray()),
            ["stream"] = stream,
            ["options"] = options
        };
    }

    internal static OllamaChatChunk ParseChunk(string line)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException error)
        {
            throw new OllamaException("Unexpected answer from Ollama.", error);
        }

        if (node?["error"]?.GetValue<string>() is { } errorMessage)
            throw new OllamaException($"Ollama error: {errorMessage}");

        var content = node?["message"]?["content"]?.GetValue<string>() ?? "";
        if (node?["done"]?.GetValue<bool>() != true)
            return new OllamaChatChunk(content);

        var metrics = new OllamaMetrics(
            node["prompt_eval_count"]?.GetValue<int>() ?? 0,
            node["eval_count"]?.GetValue<int>() ?? 0,
            TimeSpan.FromTicks((node["total_duration"]?.GetValue<long>() ?? 0) / 100),
            node["done_reason"]?.GetValue<string>());

        return new OllamaChatChunk(content, metrics);
    }

    private Uri Endpoint(string path) => new(new Uri(Options.BaseUrl.TrimEnd('/') + "/"), path.TrimStart('/'));

    private async Task<OllamaException> ErrorFromResponse(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        string? detail = null;

        try
        {
            detail = JsonNode.Parse(body)?["error"]?.GetValue<string>();
        }
        catch (JsonException)
        {
        }

        detail ??= string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body.Trim();

        if (response.StatusCode == HttpStatusCode.NotFound && detail?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
            detail += $" — install it with: ollama pull {Options.Model}";

        return new OllamaException($"Ollama error ({(int)response.StatusCode}): {detail}");
    }
}
