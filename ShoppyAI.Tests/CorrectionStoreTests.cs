using ShoppyAI.Core.Language;

namespace ShoppyAI.Tests;

public class CorrectionStoreTests
{
    private static string NewFile() => Path.Combine(TestData.NewTempDirectory(), "language", "corrections.json");

    [Fact]
    public void Corrections_are_saved_locally()
    {
        var file = NewFile();
        new CorrectionStore(file).AddOrUpdate("doka", "maintenant", "pas « bientôt »");

        var reloaded = new CorrectionStore(file);

        var correction = Assert.Single(reloaded.Active);
        Assert.Equal("doka", correction.Term);
        Assert.Equal("pas « bientôt »", correction.Note);
    }

    [Fact]
    public void Same_word_with_another_spelling_updates_instead_of_duplicating()
    {
        var store = new CorrectionStore(null);
        store.AddOrUpdate("sa7it", "merci");
        store.AddOrUpdate("Sahit", "merci beaucoup");

        var correction = Assert.Single(store.All);
        Assert.Equal("merci beaucoup", correction.Meaning);
    }

    [Fact]
    public void Disable_keeps_the_correction_and_undo_restores_it()
    {
        var store = new CorrectionStore(null);
        store.AddOrUpdate("doka", "maintenant");

        Assert.True(store.SetEnabled("doka", false));
        Assert.Empty(store.Active);
        Assert.Single(store.All);

        Assert.NotNull(store.Undo());
        Assert.Single(store.Active);
    }

    [Fact]
    public void Every_change_can_be_undone_in_order()
    {
        var store = new CorrectionStore(null);
        store.AddOrUpdate("doka", "maintenant");
        store.AddOrUpdate("doka", "tout de suite");
        store.Remove("doka");

        Assert.Empty(store.All);
        store.Undo();
        Assert.Equal("tout de suite", Assert.Single(store.All).Meaning);
        store.Undo();
        Assert.Equal("maintenant", Assert.Single(store.All).Meaning);
        store.Undo();
        Assert.Empty(store.All);
        Assert.Null(store.Undo());
    }

    [Fact]
    public void Undo_history_survives_a_restart()
    {
        var file = NewFile();
        var store = new CorrectionStore(file);
        store.AddOrUpdate("doka", "maintenant");
        store.Remove("doka");

        var reloaded = new CorrectionStore(file);
        Assert.True(reloaded.CanUndo);
        reloaded.Undo();

        Assert.Single(new CorrectionStore(file).Active);
    }

    [Fact]
    public void Unreadable_file_is_kept_as_backup_and_not_overwritten()
    {
        var file = NewFile();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ broken");

        var store = new CorrectionStore(file);
        store.AddOrUpdate("doka", "maintenant");

        Assert.NotNull(store.LoadError);
        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file)!, "corrections.json.unreadable-*"));
        Assert.Equal("{ broken", File.ReadAllText(backup));
    }

    [Fact]
    public void Rejects_empty_values()
    {
        var store = new CorrectionStore(null);

        Assert.Throws<ArgumentException>(() => store.AddOrUpdate(" ", "x"));
        Assert.Throws<ArgumentException>(() => store.AddOrUpdate("doka", ""));
    }
}

public class LanguageCommandsTests
{
    private readonly CorrectionStore _store = new(null);
    private ResponseLanguage _language = ResponseLanguage.Auto;

    private LanguageCommands Commands => new(_store, () => _language, l => _language = l);

    [Fact]
    public void Correct_list_disable_undo_forget()
    {
        Assert.Contains("Saved", Commands.TryExecute("/correct doka = maintenant | pas bientôt"));
        Assert.Contains("\"doka\" = maintenant — pas bientôt", Commands.TryExecute("/corrections"));

        Commands.TryExecute("/disable doka");
        Assert.Empty(_store.Active);

        Assert.Contains("Undone", Commands.TryExecute("/undo"));
        Assert.Single(_store.Active);

        Commands.TryExecute("/forget doka");
        Assert.Empty(_store.All);
    }

    [Fact]
    public void French_aliases_work()
    {
        Commands.TryExecute("/corriger \"bezzaf\" = beaucoup");
        Assert.Equal("bezzaf", Assert.Single(_store.All).Term);

        Commands.TryExecute("/annuler");
        Assert.Empty(_store.All);
    }

    [Theory]
    [InlineData("/lang fr", ResponseLanguage.French)]
    [InlineData("/lang darija", ResponseLanguage.DarijaLatin)]
    [InlineData("/lang darija-ar", ResponseLanguage.DarijaArabic)]
    [InlineData("/langue auto", ResponseLanguage.Auto)]
    public void Sets_the_reply_language(string command, ResponseLanguage expected)
    {
        _language = ResponseLanguage.English;
        Commands.TryExecute(command);
        Assert.Equal(expected, _language);
    }

    [Theory]
    [InlineData("wach rak ?")]
    [InlineData("/unknown-command")]
    [InlineData("git status")]
    public void Other_input_is_not_a_language_command(string input)
    {
        Assert.Null(Commands.TryExecute(input));
    }

    [Fact]
    public void Bad_usage_explains_the_syntax()
    {
        Assert.Contains("Usage", Commands.TryExecute("/correct doka"));
        Assert.Contains("Unknown language", Commands.TryExecute("/lang klingon"));
    }
}
