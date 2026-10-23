using ShoppyAI.Core.Language;
using ShoppyAI.Core.Text;

namespace ShoppyAI.Tests;

public class GlossaryTests
{
    private static DarijaGlossary Glossary => TestData.Resources.Glossary;

    private static List<string> Terms(params string[] texts) => Glossary.FindMatches(texts).Select(e => e.Term).ToList();

    [Fact]
    public void Embedded_glossary_is_complete()
    {
        Assert.True(Glossary.Entries.Count >= 100);
        Assert.All(Glossary.Entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Meaning));
            Assert.NotEmpty(e.Forms);
        });
    }

    [Fact]
    public void Single_word_forms_do_not_belong_to_two_entries()
    {
        var owners = Glossary.Entries
            .SelectMany(e => e.Forms.Append(e.Term)
                .Where(f => TextNormalizer.Tokenize(f).Count == 1)
                .Select(f => (Key: TextNormalizer.DarijaKey(f), e.Term)))
            .Distinct()
            .GroupBy(x => x.Key)
            .Where(g => g.Select(x => x.Term).Distinct().Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.Term))}")
            .ToList();

        Assert.Empty(owners);
    }

    [Fact]
    public void Finds_words_and_expressions()
    {
        var terms = Terms("wach rak dir ?");

        Assert.Contains("wach rak", terms);
        Assert.Contains("dir", terms);
    }

    [Theory]
    [InlineData("wesh rak khouya", "wach rak")]
    [InlineData("sa7it bezzef", "sahit")]
    [InlineData("ya3tik saha", "sahit")]
    [InlineData("waaaach", "wach")]
    [InlineData("وراني نخدم", "rani")]
    [InlineData("واش راك؟", "wach rak")]
    [InlineData("ما يخدمش البرنامج", "khdem")]
    [InlineData("chno bghiti daba", "daba")]
    public void Matches_spelling_variants_and_both_scripts(string text, string expectedTerm)
    {
        Assert.Contains(expectedTerm, Terms(text));
    }

    [Fact]
    public void Latest_message_entries_come_first_and_the_list_is_capped()
    {
        var matches = Glossary.FindMatches(["doka", "wach rak dir bezzaf chwiya mlih"], maxEntries: 2);

        Assert.Equal(2, matches.Count);
        Assert.Equal("doka", matches[0].Term);
    }

    [Fact]
    public void User_glossary_overrides_an_entry()
    {
        var user = DarijaGlossary.Parse("""
            { "entries": [ { "term": "doka", "forms": ["doka"], "meaning": "maintenant (version utilisateur)" } ] }
            """);

        var merged = Glossary.MergeWith(user);

        Assert.Single(merged.Entries, e => e.Term == "doka");
        Assert.Equal("maintenant (version utilisateur)", merged.FindMatches(["doka"]).Single().Meaning);
    }

    [Fact]
    public void Unreadable_user_glossary_falls_back_to_the_default()
    {
        var directory = TestData.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, LanguageResources.UserGlossaryFileName), "{ not json");

        var resources = LanguageResources.Load(directory);

        Assert.NotNull(resources.UserGlossaryError);
        Assert.Equal(Glossary.Entries.Count, resources.Glossary.Entries.Count);
    }
}
