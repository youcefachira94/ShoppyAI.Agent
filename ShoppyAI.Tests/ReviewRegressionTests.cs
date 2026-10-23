using ShoppyAI.Core.Chat;
using ShoppyAI.Core.Language;
using ShoppyAI.Core.Markdown;
using ShoppyAI.Core.Ollama;
using ShoppyAI.Core.Text;

namespace ShoppyAI.Tests;

/// <summary>Problems found by the independent review of the first version.</summary>
public class ReviewRegressionTests
{
    private static IEnumerable<MdInline> Descendants(IEnumerable<MdInline> inlines) => inlines.SelectMany(i => i switch
    {
        MdStrong s => Descendants(s.Children).Prepend(i),
        MdEmphasis e => Descendants(e.Children).Prepend(i),
        MdStrikethrough s => Descendants(s.Children).Prepend(i),
        MdLink l => Descendants(l.Children).Prepend(i),
        _ => [i]
    });

    private static MdParagraph Paragraph(string markdown) =>
        Assert.IsType<MdParagraph>(Assert.Single(SafeMarkdown.Parse(markdown)));

    [Theory]
    [InlineData("[![build](https://img.shields.io/b.svg)](https://github.com/x/y)")]
    [InlineData("[x <https://c.com> y](https://d.com)")]
    public void Links_never_contain_links(string markdown)
    {
        var outer = Assert.IsType<MdLink>(Assert.Single(Paragraph(markdown).Inlines));

        Assert.DoesNotContain(Descendants(outer.Children), i => i is MdLink);
        Assert.NotEmpty(SafeMarkdown.ToPlainText(outer.Children));
    }

    [Fact]
    public void Unmatched_bracket_is_kept()
    {
        Assert.Equal("the array [1, 2, 3 is", SafeMarkdown.ToPlainText(Paragraph("the array [1, 2, 3 is").Inlines));
    }

    [Fact]
    public void Single_tilde_is_text_and_double_tilde_is_strikethrough()
    {
        var single = Paragraph("between ~5 and 10~ minutes");
        Assert.DoesNotContain(Descendants(single.Inlines), i => i is MdStrikethrough);
        Assert.Equal("between ~5 and 10~ minutes", SafeMarkdown.ToPlainText(single.Inlines));

        Assert.Contains(Descendants(Paragraph("~~old~~ new").Inlines), i => i is MdStrikethrough);
        Assert.Equal("x^2^ a++b++ c==d==", SafeMarkdown.ToPlainText(Paragraph("x^2^ a++b++ c==d==").Inlines));
    }

    [Fact]
    public void Deep_nesting_keeps_the_text()
    {
        var emphasis = string.Concat(Enumerable.Repeat("*a ", 20)) + "deep" + string.Concat(Enumerable.Repeat(" a*", 20));
        var emphasisText = string.Concat(SafeMarkdown.Parse(emphasis).OfType<MdParagraph>().Select(p => SafeMarkdown.ToPlainText(p.Inlines)));
        Assert.Contains("deep", emphasisText);
        Assert.DoesNotContain("Markdig", emphasisText);

        var list = string.Join("\n", Enumerable.Range(0, 20).Select(i => new string(' ', i * 2) + "- item" + i));
        Assert.Contains("item19", Flatten(SafeMarkdown.Parse(list)));
    }

    [Fact]
    public void Inline_code_does_not_decide_the_direction()
    {
        var paragraph = Paragraph("باش تبني المشروع دير `dotnet build ShoppyAI.slnx -p:EnableWindowsTargeting=true` ومبعد شوف");

        Assert.True(TextDirection.IsRightToLeft(SafeMarkdown.ToDirectionText(paragraph.Inlines)));
        Assert.True(TextDirection.IsRightToLeft("باش تبني المشروع دير `dotnet build ShoppyAI.slnx -p:EnableWindowsTargeting=true` ومبعد شوف"));
        Assert.True(TextDirection.IsRightToLeft("شوف هنا https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview"));
    }

    [Fact]
    public void Enable_without_change_is_reported_and_not_undoable()
    {
        var store = new CorrectionStore(null);
        var commands = new LanguageCommands(store, new LanguagePreferences(store: null));
        commands.TryExecute("/correct doka = maintenant");

        Assert.Contains("already enabled", commands.TryExecute("/enable doka"));
        Assert.Contains("No correction", commands.TryExecute("/disable bezzaf"));
        commands.TryExecute("/disable doka");
        Assert.Contains("already disabled", commands.TryExecute("/disable doka"));

        commands.TryExecute("/undo");
        Assert.Single(store.Active);
    }

    [Fact]
    public void Renaming_a_correction_does_not_duplicate_it()
    {
        var store = new CorrectionStore(null);
        var doka = store.AddOrUpdate("doka", "maintenant");

        store.Update(doka.Id, "dorka", "maintenant", null);

        var renamed = Assert.Single(store.All);
        Assert.Equal("dorka", renamed.Term);
        Assert.Equal(doka.Id, renamed.Id);

        store.Undo();
        Assert.Equal("doka", Assert.Single(store.All).Term);
    }

    [Fact]
    public void Renaming_onto_another_correction_is_refused()
    {
        var store = new CorrectionStore(null);
        var doka = store.AddOrUpdate("doka", "maintenant");
        store.AddOrUpdate("bezzaf", "beaucoup");

        Assert.Throws<ArgumentException>(() => store.Update(doka.Id, "bezzaf", "x", null));
        Assert.Equal(2, store.All.Count);
    }

    [Fact]
    public void Failed_save_is_reported_and_rolled_back()
    {
        var blocker = Path.Combine(TestData.NewTempDirectory(), "language");
        File.WriteAllText(blocker, "a file where the folder should be");
        var store = new CorrectionStore(Path.Combine(blocker, "corrections.json"));
        var commands = new LanguageCommands(store, new LanguagePreferences(store: null));

        Assert.ThrowsAny<IOException>(() => store.AddOrUpdate("doka", "maintenant"));
        Assert.Empty(store.All);
        Assert.False(store.CanUndo);
        Assert.Contains("could not be saved", commands.TryExecute("/correct doka = maintenant"));
    }

    [Fact]
    public void Unreadable_but_not_corrupt_file_is_never_overwritten_or_renamed()
    {
        var directory = TestData.NewTempDirectory();
        var path = Path.Combine(directory, "corrections.json");
        Directory.CreateDirectory(path); // reading it fails with an access error, not a JSON error

        var store = new CorrectionStore(path);
        store.AddOrUpdate("doka", "maintenant");

        Assert.NotNull(store.LoadError);
        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.GetFiles(directory, "*.unreadable-*"));
        Assert.Single(store.All);
    }

    [Fact]
    public void Two_instances_on_the_same_file_keep_each_other_s_changes()
    {
        var path = Path.Combine(TestData.NewTempDirectory(), "corrections.json");
        var console = new CorrectionStore(path);
        var desktop = new CorrectionStore(path);

        console.AddOrUpdate("doka", "maintenant");
        desktop.AddOrUpdate("bezzaf", "beaucoup");

        Assert.Equal(2, new CorrectionStore(path).All.Count);
        Assert.Equal(2, console.All.Count);
    }

    [Fact]
    public async Task Corrections_used_in_the_conversation_reach_the_prompt_even_beyond_the_limit()
    {
        var store = new CorrectionStore(null);
        for (var i = 0; i < SystemPromptBuilder.MaxCorrections + 5; i++)
            store.AddOrUpdate($"w{i}", $"sens {i}");

        var handler = FakeOllamaHandler.Answering("ok");
        var session = new ChatSession(new OllamaChatClient(new HttpClient(handler), new OllamaOptions()),
            TestData.Resources, store, new LanguagePreferences(store: null));

        await session.ReplyAsync("wach ma3na w2 w w44 ?");

        var system = FakeOllamaHandler.Messages(handler.Requests[0].Body)[0].Content;
        Assert.Contains("\"w44\" = sens 44", system);
        Assert.Contains("\"w2\" = sens 2", system);
        Assert.Contains("\"w43\" = sens 43", system);
    }

    private static string Flatten(IEnumerable<MdBlock> blocks) => string.Concat(blocks.Select(b => b switch
    {
        MdParagraph p => SafeMarkdown.ToPlainText(p.Inlines),
        MdHeading h => SafeMarkdown.ToPlainText(h.Inlines),
        MdList l => string.Concat(l.Items.Select(Flatten)),
        MdQuote q => Flatten(q.Blocks),
        MdCodeBlock c => c.Code,
        _ => ""
    }));
}
