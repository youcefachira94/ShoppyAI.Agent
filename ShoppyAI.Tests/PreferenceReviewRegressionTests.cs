using ShoppyAI.Core.Chat;
using ShoppyAI.Core.Language;
using ShoppyAI.Core.Ollama;
using ShoppyAI.Core.Settings;

namespace ShoppyAI.Tests;

/// <summary>Problems found by the independent review of V1.1 (reply preferences).</summary>
public class PreferenceReviewRegressionTests
{
    private static LanguageInstruction? Find(string text) => TestData.Resources.Instructions.Find(text);

    private static ChatSession NewSession(LanguagePreferences preferences, FakeOllamaHandler? handler = null) =>
        new(new OllamaChatClient(new HttpClient(handler ?? new FakeOllamaHandler(_ => FakeOllamaHandler.Stream("ok"))), new OllamaOptions()),
            TestData.Resources, new CorrectionStore(null), preferences);

    private static async Task<ReplyDecision> Say(ChatSession session, string text) => (await session.ReplyAsync(text)).Request.Reply;

    [Theory]
    [InlineData("pourquoi tu réponds toujours en anglais ?")]
    [InlineData("why do you always answer in English?")]
    [InlineData("dima tjawebni b l'anglais")]
    [InlineData("ديما تجاوبني بالانجليزية")]
    [InlineData("Mon API répond en anglais alors que le site est en français")]
    [InlineData("the chatbot replies in French instead of English")]
    [InlineData("I need to reply to my client in French, can you help?")]
    [InlineData("Je dois répondre à un client en anglais")]
    [InlineData("Pourquoi tu ne réponds jamais en darija ?")]
    public void Complaints_and_descriptions_are_not_requests(string text)
    {
        Assert.Null(Find(text));
    }

    [Theory]
    [InlineData("لا تجاوبني بالفرنسية", ReplyLanguage.French)]
    [InlineData("never answer in English", ReplyLanguage.English)]
    [InlineData("jamais en anglais stp", ReplyLanguage.English)]
    [InlineData("machi b l'anglais", ReplyLanguage.English)]
    public void Negations_exclude_the_language(string text, ReplyLanguage avoided)
    {
        var instruction = Find(text);

        Assert.NotNull(instruction);
        Assert.Null(instruction.Preference.Language);
        Assert.Equal(avoided, instruction.Avoid);
    }

    [Theory]
    [InlineData("et en arabe ?")]
    [InlineData("Latin America?")]
    [InlineData("latin-1 ?")]
    [InlineData("clavier arabe ?")]
    [InlineData("french keyboard")]
    [InlineData("auto save ?")]
    [InlineData("File name too long")]
    [InlineData("path too long")]
    public void Short_unrelated_messages_are_not_requests(string text)
    {
        Assert.Null(Find(text));
    }

    [Theory]
    [InlineData("tu peux parler en français ?", ReplyLanguage.French)]
    [InlineData("Can you speak French?", ReplyLanguage.French)]
    [InlineData("let's talk in English", ReplyLanguage.English)]
    [InlineData("احكي معايا بالعربي", ReplyLanguage.Darija)]
    [InlineData("ok of course, reply in English", ReplyLanguage.English)]
    [InlineData("réponds en français, je suis en cours", ReplyLanguage.French)]
    [InlineData("I want you to answer in English", ReplyLanguage.English)]
    [InlineData("merci de répondre en français", ReplyLanguage.French)]
    public void Common_phrasings_are_recognized(string text, ReplyLanguage expected)
    {
        Assert.Equal(expected, Find(text)?.Preference.Language);
    }

    [Theory]
    [InlineData("je veux apprendre l'anglais")]
    [InlineData("learn to speak English")]
    [InlineData("je cherche un cours d'anglais")]
    public void Learning_a_language_is_not_a_request(string text)
    {
        Assert.Null(Find(text));
    }

    [Fact]
    public void A_lasting_exclusion_is_kept_for_the_conversation_and_not_shown_as_saved()
    {
        var instruction = Find("from now on don't answer in English");

        Assert.NotNull(instruction);
        Assert.Equal(PreferenceScope.Conversation, instruction.Scope);
        Assert.Equal(ReplyLanguage.English, instruction.Avoid);
    }

    [Theory]
    [InlineData("hello", "kifach ndir git push l github ? rani bloqué", ReplyLanguage.Darija)]
    [InlineData("bonjour", "How do I configure a reverse proxy with nginx for my API?", ReplyLanguage.English)]
    public async Task A_short_first_message_does_not_fix_the_language(string first, string second, ReplyLanguage expected)
    {
        var session = NewSession(new LanguagePreferences(store: null));
        await Say(session, first);

        Assert.Equal(expected, (await Say(session, second)).Language);
    }

    [Fact]
    public async Task Reply_in_my_language_follows_the_next_message_at_once()
    {
        var session = NewSession(new LanguagePreferences(store: null));
        await Say(session, "Bonjour, j'ai une question sur Git.");
        await Say(session, "réponds dans ma langue");

        Assert.Equal(ReplyLanguage.Darija, (await Say(session, "kifach ndir rebase ? rani bloqué")).Language);
    }

    [Fact]
    public void Unreadable_settings_are_never_overwritten_with_defaults()
    {
        var file = Path.Combine(TestData.NewTempDirectory(), "settings.json");
        var store = new SettingsStore(file);
        store.Update(s =>
        {
            s.Ollama.Model = "my-model:7b";
            s.Preferences.Language = ReplyLanguage.French;
        });

        // Another program (or a hand edit) leaves the file briefly invalid.
        File.WriteAllText(file, "{ \"ollama\": ");
        Assert.Equal("my-model:7b", store.Settings.Ollama.Model);
        Assert.ThrowsAny<IOException>(() => store.Update(s => s.Preferences.Script = ReplyScript.Arabic));
        Assert.Equal("{ \"ollama\": ", File.ReadAllText(file));

        // Once fixed, it is read again.
        File.WriteAllText(file, """{ "ollama": { "model": "fixed:14b" }, "preferences": { "language": "English" } }""");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal("fixed:14b", store.Settings.Ollama.Model);
    }

    [Fact]
    public void Invalid_settings_at_start_are_not_overwritten()
    {
        var file = Path.Combine(TestData.NewTempDirectory(), "settings.json");
        File.WriteAllText(file, "{ broken");

        var store = new SettingsStore(file);

        Assert.NotNull(store.LoadError);
        Assert.ThrowsAny<IOException>(() => store.Update(s => s.Preferences.Language = ReplyLanguage.French));
        Assert.Equal("{ broken", File.ReadAllText(file));
    }

    [Fact]
    public void Remember_off_in_one_process_is_respected_by_the_other()
    {
        var file = Path.Combine(TestData.NewTempDirectory(), "settings.json");
        var first = new LanguagePreferences(new SettingsStore(file));
        var second = new LanguagePreferences(new SettingsStore(file));
        first.Apply(new ReplyPreference(ReplyLanguage.French));

        first.SetRemember(false);
        second.Apply(new ReplyPreference(Script: ReplyScript.Arabic));

        Assert.False(second.Remember);
        Assert.False(new LanguagePreferences(new SettingsStore(file)).Remember);
        Assert.True(new LanguagePreferences(new SettingsStore(file)).Saved.IsEmpty);
    }

    [Fact]
    public void A_preference_that_cannot_be_saved_still_applies_until_shoppyai_closes()
    {
        var blocker = Path.Combine(TestData.NewTempDirectory(), "blocked");
        File.WriteAllText(blocker, "a file where the settings folder should be");
        var preferences = new LanguagePreferences(new SettingsStore(Path.Combine(blocker, "settings.json")));

        Assert.ThrowsAny<IOException>(() => preferences.Apply(new ReplyPreference(ReplyLanguage.French)));
        Assert.Equal(ReplyLanguage.French, preferences.Saved.Language);

        Assert.ThrowsAny<IOException>(() => preferences.SetRemember(false));
        Assert.True(preferences.Remember);
    }

    [Fact]
    public async Task Saved_note_and_save_error_belong_to_the_last_answer_only()
    {
        var answers = new Queue<string>(["ok", "   "]);
        var handler = new FakeOllamaHandler(_ => FakeOllamaHandler.Stream(answers.Dequeue()));
        var session = NewSession(new LanguagePreferences(store: null), handler);

        await session.ReplyAsync("من اليوم جاوبني بالعربية");
        Assert.NotNull(session.LastReply?.Instruction);

        await session.ReplyAsync("wach rak");
        Assert.Null(session.LastReply);
        Assert.Null(session.PreferenceSaveError);
    }

    [Fact]
    public async Task Command_reports_a_preference_kept_for_the_session_when_it_cannot_be_saved()
    {
        var blocker = Path.Combine(TestData.NewTempDirectory(), "blocked");
        File.WriteAllText(blocker, "x");
        var preferences = new LanguagePreferences(new SettingsStore(Path.Combine(blocker, "settings.json")));
        var commands = new LanguageCommands(new CorrectionStore(null), preferences);

        var result = commands.TryExecute("/lang fr");

        Assert.Contains("until ShoppyAI is closed", result);
        Assert.Equal(ReplyLanguage.French, preferences.Saved.Language);
        await Task.CompletedTask;
    }
}
