using System.Net;
using ShoppyAI.Core.Chat;
using ShoppyAI.Core.Conversations;
using ShoppyAI.Core.Language;
using ShoppyAI.Core.Ollama;

namespace ShoppyAI.Tests;

public class ChatSessionTests
{
    private readonly CorrectionStore _corrections = new(null);
    private readonly LanguagePreferences _preferences = new(store: null);

    private (ChatSession Session, FakeOllamaHandler Handler) Create(FakeOllamaHandler handler, OllamaOptions? options = null)
    {
        var client = new OllamaChatClient(new HttpClient(handler), options ?? new OllamaOptions());
        return (new ChatSession(client, TestData.Resources, _corrections, _preferences), handler);
    }

    private static async Task<string> Collect(IAsyncEnumerable<string> stream)
    {
        var text = "";
        await foreach (var delta in stream)
            text += delta;
        return text;
    }

    [Fact]
    public async Task Uses_the_chat_api_with_a_system_message_and_the_existing_model()
    {
        var (session, handler) = Create(FakeOllamaHandler.Answering("Labas", " hamdoullah"));

        var answer = await Collect(session.StreamReplyAsync("wach rak dir ?"));

        Assert.Equal("Labas hamdoullah", answer);
        var (uri, body) = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:11434/api/chat", uri.ToString());
        Assert.Equal("qwen2.5-coder:14b", body["model"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal(8192, body["options"]!["num_ctx"]!.GetValue<int>());

        var messages = FakeOllamaHandler.Messages(body);
        Assert.Equal("system", messages[0].Role);
        Assert.StartsWith("You are ShoppyAI", messages[0].Content);
        Assert.Contains("Reply language: Algerian Darija written in Latin letters", messages[0].Content);
        Assert.Equal(("user", "wach rak dir ?"), messages[^1]);
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public async Task Previous_messages_are_sent_with_their_roles_in_order()
    {
        var replies = new Queue<string>(["Mlih! Desktop wela web?", "WPF mlih l desktop."]);
        var (session, handler) = Create(new FakeOllamaHandler(_ => FakeOllamaHandler.Stream(replies.Dequeue())));

        await Collect(session.StreamReplyAsync("rani hab ndir application"));
        await Collect(session.StreamReplyAsync("desktop, b WPF"));

        var messages = FakeOllamaHandler.Messages(handler.Requests[1].Body);
        Assert.Equal(["system", "user", "assistant", "user"], messages.Select(m => m.Role));
        Assert.Equal("rani hab ndir application", messages[1].Content);
        Assert.Equal("Mlih! Desktop wela web?", messages[2].Content);
        Assert.Equal("desktop, b WPF", messages[3].Content);
        Assert.Equal(4, session.History.Count);
    }

    [Fact]
    public async Task History_only_contains_real_messages()
    {
        var (session, _) = Create(FakeOllamaHandler.Answering("Sahla."));

        await Collect(session.StreamReplyAsync("kifach nconnecti Supabase maa WPF ?"));

        Assert.Equal([ChatRole.User, ChatRole.Assistant], session.History.Select(m => m.Role));
        Assert.DoesNotContain(session.History, m => m.Content.Contains("Thinking", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(session.History, m => m.Role == ChatRole.System);
    }

    [Fact]
    public async Task Failed_request_leaves_the_history_unchanged()
    {
        var (session, _) = Create(new FakeOllamaHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""{"error":"out of memory"}""")
        }));

        var error = await Assert.ThrowsAsync<OllamaException>(() => Collect(session.StreamReplyAsync("wach rak")));

        Assert.Contains("out of memory", error.Message);
        Assert.Empty(session.History);
    }

    [Fact]
    public async Task Missing_model_error_explains_how_to_install_it()
    {
        var (session, _) = Create(new FakeOllamaHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"error":"model 'qwen2.5-coder:14b' not found"}""")
        }));

        var error = await Assert.ThrowsAsync<OllamaException>(() => session.ReplyAsync("salam"));

        Assert.Contains("ollama pull qwen2.5-coder:14b", error.Message);
    }

    [Fact]
    public async Task Error_inside_the_stream_is_reported()
    {
        var (session, _) = Create(new FakeOllamaHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"message\":{\"role\":\"assistant\",\"content\":\"La\"},\"done\":false}\n{\"error\":\"runner crashed\"}\n")
        }));

        var error = await Assert.ThrowsAsync<OllamaException>(() => Collect(session.StreamReplyAsync("salam")));

        Assert.Contains("runner crashed", error.Message);
        Assert.Empty(session.History);
    }

    [Fact]
    public async Task Cancelled_answer_is_not_recorded()
    {
        var (session, _) = Create(FakeOllamaHandler.Answering("Labas", " w nta", "?"));
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in session.StreamReplyAsync("wach rak", cancellation.Token))
                cancellation.Cancel();
        });

        Assert.Empty(session.History);
    }

    [Fact]
    public async Task Reset_during_an_answer_starts_a_clean_conversation()
    {
        var (session, _) = Create(FakeOllamaHandler.Answering("Labas", " hamdoullah"));

        await foreach (var _ in session.StreamReplyAsync("wach rak"))
            session.Reset();

        Assert.Empty(session.History);
    }

    [Fact]
    public async Task Short_message_keeps_the_language_of_the_conversation()
    {
        var (session, handler) = Create(new FakeOllamaHandler(_ => FakeOllamaHandler.Stream("Mlih.")));

        await Collect(session.StreamReplyAsync("3lach ma ykhdemch ?"));
        await Collect(session.StreamReplyAsync("ok"));

        var system = FakeOllamaHandler.Messages(handler.Requests[1].Body)[0].Content;
        Assert.Contains("Reply language: Algerian Darija written in Latin letters", system);
    }

    [Fact]
    public async Task Short_follow_up_in_french_words_does_not_switch_a_darija_conversation()
    {
        var (session, handler) = Create(new FakeOllamaHandler(_ => FakeOllamaHandler.Stream("Mlih.")));

        await Collect(session.StreamReplyAsync("rani hab ndir application desktop l gestion ta3 stock"));
        await Collect(session.StreamReplyAsync("w la base de données ?"));

        var system = FakeOllamaHandler.Messages(handler.Requests[1].Body)[0].Content;
        Assert.Contains("Reply language: Algerian Darija written in Latin letters", system);
        Assert.Contains("This is the language of the conversation", system);
    }

    [Fact]
    public async Task Clear_french_request_switches_and_darija_switches_back()
    {
        var (session, handler) = Create(new FakeOllamaHandler(_ => FakeOllamaHandler.Stream("Ok.")));

        await Collect(session.StreamReplyAsync("wach rak ?"));
        await Collect(session.StreamReplyAsync("Peux-tu me répondre en français s'il te plaît ? Comment créer un fichier .gitignore ?"));
        await Collect(session.StreamReplyAsync("w doka 3awed b darija"));

        Assert.Contains("Reply language: French. The user just asked for this", FakeOllamaHandler.Messages(handler.Requests[1].Body)[0].Content);
        Assert.Contains("Reply language: Algerian Darija written in Latin letters", FakeOllamaHandler.Messages(handler.Requests[2].Body)[0].Content);
    }

    [Fact]
    public async Task Preference_and_corrections_reach_the_system_message()
    {
        _preferences.Apply(new ReplyPreference(ReplyLanguage.Darija, ReplyScript.Arabic));
        _corrections.AddOrUpdate("doka", "maintenant", "pas bientôt");
        _corrections.AddOrUpdate("bark", "seulement");
        _corrections.SetEnabled("bark", false);
        var (session, handler) = Create(FakeOllamaHandler.Answering("درك"));

        await session.ReplyAsync("9oli wach ndir doka");

        var system = FakeOllamaHandler.Messages(handler.Requests[0].Body)[0].Content;
        Assert.Contains("Reply language: Algerian Darija written in Arabic script", system);
        Assert.Contains("- \"doka\" = maintenant (pas bientôt)", system);
        Assert.DoesNotContain("\"bark\" = seulement", system);
        Assert.Contains("# Glossary for words in this conversation", system);
        Assert.Contains("- doka", system);
    }

    [Fact]
    public async Task Old_messages_are_dropped_when_the_context_is_full()
    {
        var options = new OllamaOptions { ContextLength = 3000, ReservedAnswerTokens = 200 };
        var (session, handler) = Create(new FakeOllamaHandler(_ => FakeOllamaHandler.Stream(new string('x', 1500))), options);

        for (var i = 0; i < 4; i++)
            await Collect(session.StreamReplyAsync($"message {i}"));

        var messages = FakeOllamaHandler.Messages(handler.Requests[^1].Body);
        Assert.Equal("system", messages[0].Role);
        Assert.Equal("user", messages[1].Role);
        Assert.True(messages.Count < 8);
        Assert.Contains("oldest messages of this conversation were removed", messages[0].Content);
        Assert.Equal("message 3", messages[^1].Content);
    }

    [Fact]
    public async Task Only_one_answer_at_a_time()
    {
        var gate = new TaskCompletionSource();
        var (session, _) = Create(new FakeOllamaHandler(_ =>
        {
            gate.Task.Wait();
            return FakeOllamaHandler.Stream("ok");
        }));

        var first = Task.Run(() => session.ReplyAsync("salam"));
        await Task.Delay(100);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReplyAsync("salam 2"));
        gate.SetResult();
        await first;
    }
}

public class ContextWindowTests
{
    [Fact]
    public void Keeps_everything_that_fits()
    {
        var history = new[] { ChatMessage.User("a"), ChatMessage.Assistant("b") };

        var (messages, dropped) = ContextWindow.SelectRecent(history, 1000);

        Assert.Equal(history, messages);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void Drops_the_oldest_and_never_starts_with_an_answer()
    {
        var history = new[]
        {
            ChatMessage.User(new string('a', 100)), ChatMessage.Assistant(new string('b', 100)),
            ChatMessage.User(new string('c', 100)), ChatMessage.Assistant(new string('d', 100))
        };

        var (messages, dropped) = ContextWindow.SelectRecent(history, 140);

        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Equal(2, dropped);
        Assert.Equal(history[2..], messages);
    }

    [Fact]
    public void Zero_budget_keeps_nothing()
    {
        var (messages, dropped) = ContextWindow.SelectRecent([ChatMessage.User("a")], 0);

        Assert.Empty(messages);
        Assert.Equal(1, dropped);
    }
}

public class SystemPromptBuilderTests
{
    private static readonly SystemPromptBuilder Builder = new(TestData.Resources);

    private static PromptContext Context(string text, ReplyPreference? saved = null)
    {
        var detection = TestData.Detector.Detect(text);
        var reply = new ConversationLanguage().Decide(detection, TestData.Resources.Instructions.Find(text), saved ?? ReplyPreference.None);
        return new PromptContext(detection, reply, TestData.Resources.Glossary.FindMatches([text]), []);
    }

    [Fact]
    public void Fixed_part_is_identical_on_every_turn()
    {
        var first = Builder.Build(Context("wach rak dir ?"));
        var second = Builder.Build(Context("Bonjour, comment ça va ?"));

        Assert.StartsWith(Builder.FixedPart, first);
        Assert.StartsWith(Builder.FixedPart, second);
        Assert.Contains("# Examples of the expected style", Builder.FixedPart);
        Assert.Contains("User: wach rak dir ?", Builder.FixedPart);
    }

    [Theory]
    [InlineData("wach rak dir ?", "Algerian Darija written in Latin letters")]
    [InlineData("واش راك دير؟", "Algerian Darija written in Arabic script")]
    [InlineData("ماذا يجب أن أفعل؟", "Modern Standard Arabic.")]
    [InlineData("Bonjour, comment je peux faire ?", "French. Same language as the user.")]
    [InlineData("How do I fix this?", "English. Same language as the user.")]
    public void A_new_conversation_follows_the_user(string text, string expected)
    {
        Assert.Contains($"Reply language: {expected}", Builder.Build(Context(text)));
    }

    [Fact]
    public void Saved_preference_overrides_the_detection()
    {
        var prompt = Builder.Build(Context("wach rak dir ?", new ReplyPreference(ReplyLanguage.French)));

        Assert.Contains("Reply language: French. This is the user's saved preference", prompt);
    }

    [Fact]
    public void The_model_may_follow_a_request_the_parser_missed()
    {
        Assert.Contains("If the user's latest message clearly asks for another reply language or alphabet, follow their request instead.",
            Builder.Build(Context("wach rak")));
    }

    [Fact]
    public void Other_variety_words_are_flagged_with_caution()
    {
        var prompt = Builder.Build(Context("chno bghiti daba ?"));

        Assert.Contains("\"daba\" (Moroccan)", prompt);
        Assert.Contains("do not correct the user", prompt);
    }
}
