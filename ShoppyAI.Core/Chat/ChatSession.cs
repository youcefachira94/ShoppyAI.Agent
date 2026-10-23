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
    ReplyDecision Reply,
    IReadOnlyList<GlossaryEntry> GlossaryMatches,
    int DroppedMessages)
{
    public string SystemPrompt => Messages[0].Content;
}

public sealed record ChatReply(string Content, PreparedRequest Request, OllamaMetrics? Metrics);

/// <summary>
/// One conversation with the model. The history only contains real user and assistant
/// messages (no placeholders such as "Thinking..."), is kept in memory only, and a turn
/// is recorded only once the answer is complete. The reply language follows
/// <see cref="ConversationLanguage"/>: explicit requests, then saved preferences, then the
/// language of the conversation.
/// </summary>
public sealed class ChatSession
{
    private const int UserMessagesForGlossary = 3;

    private readonly OllamaChatClient _client;
    private readonly LanguageResources _resources;
    private readonly CorrectionStore _corrections;
    private readonly LanguagePreferences _preferences;
    private readonly ConversationLanguage _language = new();
    private readonly LanguageDetector _detector;
    private readonly SystemPromptBuilder _promptBuilder;
    private readonly List<ChatMessage> _history = [];
    private int _generation;
    private int _busy;

    public ChatSession(
        OllamaChatClient client,
        LanguageResources resources,
        CorrectionStore corrections,
        LanguagePreferences preferences)
    {
        _client = client;
        _resources = resources;
        _corrections = corrections;
        _preferences = preferences;
        _detector = new LanguageDetector(resources.Markers);
        _promptBuilder = new SystemPromptBuilder(resources);
    }

    public IReadOnlyList<ChatMessage> History => _history.ToList();

    public OllamaMetrics? LastMetrics { get; private set; }

    /// <summary>Reply decision of the last completed turn (language, alphabet, and the request it contained).</summary>
    public ReplyDecision? LastReply { get; private set; }

    /// <summary>Reply preferences asked for in this conversation (not saved).</summary>
    public ReplyPreference ConversationRequests => _language.Requested;

    /// <summary>Set when a lasting preference asked for in a message could not be saved.</summary>
    public string? PreferenceSaveError { get; private set; }

    /// <summary>Raised when the requests of this conversation change.</summary>
    public event EventHandler? LanguageChanged;

    public LanguageDetector Detector => _detector;

    public void Reset()
    {
        _generation++;
        _history.Clear();
        _language.Reset();
        LastMetrics = null;
        LastReply = null;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Forgets the reply-language requests made in this conversation, e.g. when the user
    /// changes a saved preference that should apply now. With <paramref name="fields"/>,
    /// only the fields it specifies are forgotten.
    /// </summary>
    public void ResetConversationRequests(ReplyPreference? fields = null)
    {
        _language.ResetRequests(fields);
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public PreparedRequest Prepare(string userText)
    {
        var detection = _detector.Detect(userText);
        var recentUserTexts = _history
            .Where(m => m.Role == ChatRole.User)
            .Select(m => m.Content)
            .TakeLast(UserMessagesForGlossary - 1)
            .Reverse()
            .Prepend(userText)
            .ToList();
        var glossary = _resources.Glossary.FindMatches(recentUserTexts);
        var corrections = PrioritizeCorrections(_corrections.Active, recentUserTexts);
        var reply = _language.Decide(detection, _resources.Instructions.Find(userText), _preferences.Saved);
        var styleNotes = _preferences.StyleNotes;

        var systemPrompt = _promptBuilder.Build(
            new PromptContext(detection, reply, glossary, corrections, styleNotes));

        var options = _client.Options;
        var budget = options.ContextLength - options.ReservedAnswerTokens -
                     ContextWindow.EstimateTokens(systemPrompt) - ContextWindow.EstimateTokens(userText);
        var (kept, dropped) = ContextWindow.SelectRecent(_history, Math.Max(0, budget));

        if (dropped > 0)
        {
            systemPrompt = _promptBuilder.Build(
                new PromptContext(detection, reply, glossary, corrections, styleNotes, dropped));
        }

        var messages = new List<ChatMessage>(kept.Count + 2) { ChatMessage.System(systemPrompt) };
        messages.AddRange(kept);
        messages.Add(ChatMessage.User(userText));

        return new PreparedRequest(messages, detection, reply, glossary, dropped);
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
            Commit(generation, userText, answer.ToString(), prepared, metrics);
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
            Commit(generation, userText, result.Content, prepared, result.Metrics);
            return new ChatReply(result.Content.Trim(), prepared, result.Metrics);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// Corrections for words in the recent messages first, then the most recently changed
    /// ones, so the relevant ones are sent even when there are more than the prompt allows.
    /// </summary>
    private static List<LanguageCorrection> PrioritizeCorrections(
        IReadOnlyList<LanguageCorrection> corrections, IReadOnlyList<string> recentUserTexts)
    {
        var byEntry = corrections.ToDictionary(c => new GlossaryEntry(c.Term, [c.Term], c.Meaning), c => c);
        var used = new DarijaGlossary(byEntry.Keys)
            .FindMatches(recentUserTexts, maxEntries: int.MaxValue)
            .Select(entry => byEntry[entry])
            .ToList();

        return used
            .Concat(corrections.Except(used).OrderByDescending(c => c.UpdatedAt))
            .ToList();
    }

    private void EnterBusy()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            throw new InvalidOperationException("ShoppyAI is already answering a message.");
    }

    private void Commit(int generation, string userText, string answer, PreparedRequest prepared, OllamaMetrics? metrics)
    {
        LastMetrics = metrics;

        // A reset during the request starts a new conversation: the old answer must not leak into it.
        if (generation != _generation || string.IsNullOrWhiteSpace(answer))
            return;

        _history.Add(ChatMessage.User(userText));
        _history.Add(ChatMessage.Assistant(answer.Trim()));

        var instruction = prepared.Reply.Instruction;
        _language.Commit(prepared.Detection, instruction);
        LastReply = prepared.Reply;
        PreferenceSaveError = null;

        if (instruction?.Scope == PreferenceScope.Persistent)
        {
            try
            {
                _preferences.Apply(instruction.Preference);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The answer already used the preference; only saving it for later failed.
                PreferenceSaveError = error.Message;
            }
        }

        if (instruction is not null)
            LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}
