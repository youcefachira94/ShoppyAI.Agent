using System.Runtime.CompilerServices;
using System.Text;
using ShoppyAI.Core.Conversations;
using ShoppyAI.Core.Language;
using ShoppyAI.Core.Ollama;

namespace ShoppyAI.Core.Chat;

/// <summary>What is sent to Ollama for one user message.</summary>
public sealed record PreparedRequest(
    IReadOnlyList<ChatMessage> Messages,
    LanguageDetection Detection,
    IReadOnlyList<GlossaryEntry> GlossaryMatches,
    int DroppedMessages)
{
    public string SystemPrompt => Messages[0].Content;
}

public sealed record ChatReply(string Content, PreparedRequest Request, OllamaMetrics? Metrics);

/// <summary>
/// One conversation with the model. The history only contains real user and assistant
/// messages (no placeholders such as "Thinking..."), is kept in memory only, and a turn
/// is recorded only once the answer is complete.
/// </summary>
public sealed class ChatSession
{
    private const int UserMessagesForGlossary = 3;

    private readonly OllamaChatClient _client;
    private readonly LanguageResources _resources;
    private readonly CorrectionStore _corrections;
    private readonly Func<ResponseLanguage> _responseLanguage;
    private readonly LanguageDetector _detector;
    private readonly SystemPromptBuilder _promptBuilder;
    private readonly List<ChatMessage> _history = [];
    private int _generation;
    private int _busy;

    public ChatSession(
        OllamaChatClient client,
        LanguageResources resources,
        CorrectionStore corrections,
        Func<ResponseLanguage> responseLanguage)
    {
        _client = client;
        _resources = resources;
        _corrections = corrections;
        _responseLanguage = responseLanguage;
        _detector = new LanguageDetector(resources.Markers);
        _promptBuilder = new SystemPromptBuilder(resources);
    }

    public IReadOnlyList<ChatMessage> History => _history.ToList();

    /// <summary>Language of the conversation, used for short or unclear messages like "ok".</summary>
    public LanguageDetection? LastRecognizedLanguage { get; private set; }

    public OllamaMetrics? LastMetrics { get; private set; }

    public LanguageDetector Detector => _detector;

    public void Reset()
    {
        _generation++;
        _history.Clear();
        LastRecognizedLanguage = null;
        LastMetrics = null;
    }

    public PreparedRequest Prepare(string userText)
    {
        var detection = _detector.Detect(userText);
        var recentUserTexts = _history
            .Where(m => m.Role == ChatRole.User)
            .Select(m => m.Content)
            .TakeLast(UserMessagesForGlossary - 1)
            .Reverse()
            .Prepend(userText);
        var glossary = _resources.Glossary.FindMatches(recentUserTexts);
        var corrections = _corrections.Active;
        var preference = _responseLanguage();

        var systemPrompt = _promptBuilder.Build(
            new PromptContext(detection, LastRecognizedLanguage, preference, glossary, corrections));

        var options = _client.Options;
        var budget = options.ContextLength - options.ReservedAnswerTokens -
                     ContextWindow.EstimateTokens(systemPrompt) - ContextWindow.EstimateTokens(userText);
        var (kept, dropped) = ContextWindow.SelectRecent(_history, Math.Max(0, budget));

        if (dropped > 0)
        {
            systemPrompt = _promptBuilder.Build(
                new PromptContext(detection, LastRecognizedLanguage, preference, glossary, corrections, dropped));
        }

        var messages = new List<ChatMessage>(kept.Count + 2) { ChatMessage.System(systemPrompt) };
        messages.AddRange(kept);
        messages.Add(ChatMessage.User(userText));

        return new PreparedRequest(messages, detection, glossary, dropped);
    }

    /// <summary>Streams the answer. The turn is added to the history only if the answer completes.</summary>
    public async IAsyncEnumerable<string> StreamReplyAsync(
        string userText,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnterBusy();
        try
        {
            var generation = _generation;
            var prepared = Prepare(userText);
            var answer = new StringBuilder();
            OllamaMetrics? metrics = null;

            await foreach (var chunk in _client.StreamAsync(prepared.Messages, cancellationToken))
            {
                metrics = chunk.Metrics ?? metrics;
                if (chunk.Content.Length == 0)
                    continue;

                answer.Append(chunk.Content);
                yield return chunk.Content;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Commit(generation, userText, answer.ToString(), prepared.Detection, metrics);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public async Task<ChatReply> ReplyAsync(string userText, CancellationToken cancellationToken = default)
    {
        EnterBusy();
        try
        {
            var generation = _generation;
            var prepared = Prepare(userText);
            var result = await _client.CompleteAsync(prepared.Messages, cancellationToken);
            Commit(generation, userText, result.Content, prepared.Detection, result.Metrics);
            return new ChatReply(result.Content.Trim(), prepared, result.Metrics);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private void EnterBusy()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            throw new InvalidOperationException("ShoppyAI is already answering a message.");
    }

    private void Commit(int generation, string userText, string answer, LanguageDetection detection, OllamaMetrics? metrics)
    {
        LastMetrics = metrics;

        // A reset during the request starts a new conversation: the old answer must not leak into it.
        if (generation != _generation || string.IsNullOrWhiteSpace(answer))
            return;

        _history.Add(ChatMessage.User(userText));
        _history.Add(ChatMessage.Assistant(answer.Trim()));

        if (detection.Language != DetectedLanguage.Unknown && (LastRecognizedLanguage is null || !detection.IsWeak))
            LastRecognizedLanguage = detection;
    }
}
