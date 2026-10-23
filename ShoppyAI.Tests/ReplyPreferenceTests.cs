using ShoppyAI.Core.Chat;
using ShoppyAI.Core.Conversations;
using ShoppyAI.Core.Language;
using ShoppyAI.Core.Ollama;
using ShoppyAI.Core.Settings;

namespace ShoppyAI.Tests;

/// <summary>Natural multilingual conversation: reply language, alphabet and lasting preferences.</summary>
public class ReplyPreferenceTests
{
    private readonly FakeOllamaHandler _handler = new(_ => FakeOllamaHandler.Stream("ok"));

    private ChatSession NewSession(LanguagePreferences preferences) =>
        new(new OllamaChatClient(new HttpClient(_handler), new OllamaOptions()), TestData.Resources, new CorrectionStore(null), preferences);

    private async Task<ReplyDecision> Say(ChatSession session, string text) => (await session.ReplyAsync(text)).Request.Reply;

    private string LastSystemPrompt => FakeOllamaHandler.Messages(_handler.Requests[^1].Body)[0].Content;

    private static (ReplyLanguage, ReplyScript) Of(ReplyDecision reply) => (reply.Language, reply.Script);

    private static string NewSettingsFile() => Path.Combine(TestData.NewTempDirectory(), "settings.json");

    [Fact]
    public async Task Required_case_darija_then_arabic_letters_then_english_question()
    {
        var preferences = new LanguagePreferences(store: null);
        var session = NewSession(preferences);

        Assert.Equal((ReplyLanguage.Darija, ReplyScript.Latin), Of(await Say(session, "wach rak ?")));

        var switched = await Say(session, "جاوبني بالحروف العربية من الآن");
        Assert.Equal((ReplyLanguage.Darija, ReplyScript.Arabic), Of(switched));
        Assert.Equal(PreferenceScope.Persistent, switched.Instruction?.Scope);
        Assert.Contains("confirm it in a few words", LastSystemPrompt);

        var english = await Say(session, "what can you do for me?");
        Assert.Equal((ReplyLanguage.Darija, ReplyScript.Arabic), Of(english));
        Assert.Contains("Reply language: Algerian Darija written in Arabic script", LastSystemPrompt);
        Assert.Contains("Keep code, commands, file names, identifiers and technical names in their original Latin spelling", LastSystemPrompt);

        Assert.Equal(new ReplyPreference(ReplyLanguage.Darija, ReplyScript.Arabic), preferences.Saved);
        Assert.Equal(6, session.History.Count);
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant],
            session.History.Select(m => m.Role));
    }

    [Fact]
    public async Task Arabic_preference_is_not_erased_by_english_technical_questions()
    {
        var session = NewSession(new LanguagePreferences(store: null));

        await Say(session, "من اليوم جاوبني بالعربية");
        await Say(session, "how do I create a git branch?");
        var third = await Say(session, "and how do I delete it on the remote?");

        Assert.Equal((ReplyLanguage.Darija, ReplyScript.Arabic), Of(third));
        Assert.Equal(ReplySource.Saved, third.Source);
    }

    [Fact]
    public async Task Lasting_preference_is_kept_for_new_chats_and_after_a_restart()
    {
        var file = NewSettingsFile();
        var session = NewSession(new LanguagePreferences(new SettingsStore(file)));
        await Say(session, "من اليوم جاوبني بالعربية");

        session.Reset();
        Assert.Equal((ReplyLanguage.Darija, ReplyScript.Arabic), Of(await Say(session, "wach rak khoya")));

        var afterRestart = NewSession(new LanguagePreferences(new SettingsStore(file)));
        Assert.Equal((ReplyLanguage.Darija, ReplyScript.Arabic), Of(await Say(afterRestart, "hello")));
    }

    [Fact]
    public async Task Without_remember_preferences_last_until_shoppyai_is_closed()
    {
        var file = NewSettingsFile();
        var preferences = new LanguagePreferences(new SettingsStore(file));
        preferences.SetRemember(false);
        var session = NewSession(preferences);

        await Say(session, "from now on answer in English");
        session.Reset();
        Assert.Equal(ReplyLanguage.English, (await Say(session, "wach rak ?")).Language);

        var afterRestart = new LanguagePreferences(new SettingsStore(file));
        Assert.False(afterRestart.Remember);
        Assert.True(afterRestart.Saved.IsEmpty);
    }

    [Fact]
    public async Task Explicit_french_lasts_for_the_conversation_only()
    {
        var session = NewSession(new LanguagePreferences(store: null));

        await Say(session, "wach rak ?");
        Assert.Equal(ReplyLanguage.French, (await Say(session, "réponds en français")).Language);
        Assert.Equal(ReplyLanguage.French, (await Say(session, "how do I revert a commit?")).Language);
        Assert.Equal(ReplyLanguage.French, (await Say(session, "ok")).Language);

        session.Reset();
        Assert.Equal(ReplyLanguage.Darija, (await Say(session, "salam khouya")).Language);
    }

    [Theory]
    [InlineData("oui")]
    [InlineData("vasy")]
    [InlineData("saha")]
    [InlineData("ok")]
    [InlineData("👍")]
    public async Task Short_messages_do_not_change_the_language(string shortMessage)
    {
        var preferences = new LanguagePreferences(store: null);
        var session = NewSession(preferences);
        await Say(session, "rani nkhdem 3la projet WPF");

        var reply = await Say(session, shortMessage);

        Assert.Equal((ReplyLanguage.Darija, ReplyScript.Latin), Of(reply));
        Assert.Null(reply.Instruction);
        Assert.True(preferences.Saved.IsEmpty);
    }

    [Fact]
    public async Task One_message_in_another_language_keeps_the_conversation_two_switch_it()
    {
        var session = NewSession(new LanguagePreferences(store: null));
        await Say(session, "wach rak ?");

        var first = await Say(session, "Je voudrais comprendre comment fonctionne le binding en WPF.");
        Assert.Equal(ReplyLanguage.Darija, first.Language);
        Assert.Equal(ReplySource.ConversationLanguage, first.Source);
        Assert.Contains("does not change the reply language", LastSystemPrompt);

        Assert.Equal(ReplyLanguage.French, (await Say(session, "Pouvez-vous me donner un exemple avec une liste ?")).Language);
    }

    [Theory]
    [InlineData("salut, rani bloqué f une erreur, can you help?", ReplyLanguage.Darija)]
    [InlineData("kifach nconnecti Supabase maa WPF ?", ReplyLanguage.Darija)]
    [InlineData("How do I connect Supabase with WPF?", ReplyLanguage.English)]
    [InlineData("Bonjour, comment je peux installer Ollama ?", ReplyLanguage.French)]
    public async Task A_new_conversation_starts_in_the_user_s_language(string text, ReplyLanguage expected)
    {
        Assert.Equal(expected, (await Say(NewSession(new LanguagePreferences(store: null)), text)).Language);
    }

    [Fact]
    public async Task Just_this_once_applies_to_one_answer()
    {
        var session = NewSession(new LanguagePreferences(store: null));
        await Say(session, "salam, kifach ndir branch f git ?");

        Assert.Equal(ReplyLanguage.English, (await Say(session, "juste cette fois en anglais : c'est quoi un rebase ?")).Language);
        Assert.Equal(ReplyLanguage.Darija, (await Say(session, "w merge ?")).Language);
    }

    [Fact]
    public async Task Corrections_of_language_alphabet_and_length_apply_at_once_and_stay()
    {
        var session = NewSession(new LanguagePreferences(store: null));
        await Say(session, "salam");

        Assert.Equal(ReplyLanguage.French, (await Say(session, "ne réponds pas en darija, en français stp")).Language);
        Assert.Equal((ReplyLanguage.Darija, ReplyScript.Latin), Of(await Say(session, "w doka 3awed b darija")));
        Assert.Equal((ReplyLanguage.Darija, ReplyScript.Arabic), Of(await Say(session, "la, b l3arbia machi b latin")));

        var shorter = await Say(session, "trop long, plus court stp");
        Assert.Equal(AnswerLength.Short, shorter.Length);
        Assert.Equal((ReplyLanguage.Darija, ReplyScript.Arabic), Of(shorter));

        var next = await Say(session, "w Kubernetes ?");
        Assert.Equal(AnswerLength.Short, next.Length);
        Assert.Contains("Answer length: short", LastSystemPrompt);
        Assert.Equal((ReplyLanguage.Darija, ReplyScript.Arabic), Of(next));
    }

    [Fact]
    public async Task Reply_in_my_language_goes_back_to_following_the_user()
    {
        var preferences = new LanguagePreferences(store: null);
        preferences.Apply(new ReplyPreference(ReplyLanguage.Darija, ReplyScript.Arabic));
        var session = NewSession(preferences);

        Assert.Equal(ReplyScript.Arabic, (await Say(session, "hello, can you help me with Git?")).Script);
        Assert.Equal(ReplyLanguage.French, (await Say(session, "réponds dans ma langue")).Language);
        Assert.Equal(ReplyLanguage.French, (await Say(session, "comment je crée une branche ?")).Language);
    }

    [Fact]
    public async Task Preferences_can_be_reset_and_changed_by_command()
    {
        var preferences = new LanguagePreferences(store: null);
        var session = NewSession(preferences);
        var commands = new LanguageCommands(new CorrectionStore(null), preferences, session);

        await Say(session, "من اليوم جاوبني بالعربية");
        await Say(session, "réponds en français");
        Assert.Contains("This chat: Français", commands.TryExecute("/prefs"));

        Assert.Contains("Preferences reset", commands.TryExecute("/prefs reset"));
        Assert.True(preferences.Saved.IsEmpty);
        Assert.True(session.ConversationRequests.IsEmpty);

        commands.TryExecute("/alphabet arabic");
        commands.TryExecute("/length short");
        commands.TryExecute("/style tutoie-moi, sans emojis");
        var reply = await Say(session, "wach rak khoya");

        Assert.Equal((ReplyLanguage.Darija, ReplyScript.Arabic), Of(reply));
        Assert.Contains("Answer length: short", LastSystemPrompt);
        Assert.Contains("- tutoie-moi, sans emojis", LastSystemPrompt);
    }

    [Fact]
    public async Task Saved_language_from_the_sidebar_applies_even_after_a_chat_request()
    {
        var preferences = new LanguagePreferences(store: null);
        var session = NewSession(preferences);
        await Say(session, "réponds en français");

        // What the sidebar and /lang do: save, then forget this chat's request for that field.
        preferences.Apply(new ReplyPreference(ReplyLanguage.English));
        session.ResetConversationRequests(new ReplyPreference(ReplyLanguage.English));

        Assert.Equal(ReplyLanguage.English, (await Say(session, "wach rak ?")).Language);
    }

    [Fact]
    public async Task Answer_is_kept_when_the_preference_cannot_be_saved()
    {
        var blocker = Path.Combine(TestData.NewTempDirectory(), "blocked");
        File.WriteAllText(blocker, "a file where the settings folder should be");
        var session = NewSession(new LanguagePreferences(new SettingsStore(Path.Combine(blocker, "settings.json"))));

        var reply = await Say(session, "من اليوم جاوبني بالعربية");

        Assert.Equal((ReplyLanguage.Darija, ReplyScript.Arabic), Of(reply));
        Assert.Equal(2, session.History.Count);
        Assert.NotNull(session.PreferenceSaveError);
    }

    [Fact]
    public void Version_1_setting_is_migrated()
    {
        var file = NewSettingsFile();
        File.WriteAllText(file, """{ "responseLanguage": "DarijaArabic", "ollama": { "model": "qwen2.5-coder:14b" } }""");

        var store = new SettingsStore(file);
        var preferences = new LanguagePreferences(store);
        preferences.AddStyleNote("sans emojis");

        Assert.Equal(new ReplyPreference(ReplyLanguage.Darija, ReplyScript.Arabic), preferences.Saved);
        Assert.DoesNotContain("responseLanguage", File.ReadAllText(file));
        Assert.Equal("qwen2.5-coder:14b", new SettingsStore(file).Settings.Ollama.Model);
    }

    [Fact]
    public void Console_and_desktop_see_each_other_s_preferences()
    {
        var file = NewSettingsFile();
        var console = new LanguagePreferences(new SettingsStore(file));
        var desktop = new LanguagePreferences(new SettingsStore(file));

        console.Apply(new ReplyPreference(ReplyLanguage.French));
        desktop.Apply(new ReplyPreference(Script: ReplyScript.Arabic));

        Assert.Equal(new ReplyPreference(ReplyLanguage.French, ReplyScript.Arabic), console.Saved);
        Assert.Equal(new ReplyPreference(ReplyLanguage.French, ReplyScript.Arabic), new LanguagePreferences(new SettingsStore(file)).Saved);
    }

    [Fact]
    public async Task A_request_message_does_not_count_as_a_vote_to_switch()
    {
        var session = NewSession(new LanguagePreferences(store: null));
        await Say(session, "wach rak ?");
        await Say(session, "Peux-tu me répondre en français s'il te plaît ?");
        await Say(session, "w doka 3awed b darija");

        // One French message after going back to Darija must not switch (the request above is not a vote).
        Assert.Equal(ReplyLanguage.Darija, (await Say(session, "Je voudrais comprendre le binding en WPF.")).Language);
    }
}
