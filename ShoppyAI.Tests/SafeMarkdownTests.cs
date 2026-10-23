using ShoppyAI.Core.Markdown;

namespace ShoppyAI.Tests;

public class SafeMarkdownTests
{
    [Fact]
    public void Bold_and_inline_code()
    {
        var paragraph = Assert.IsType<MdParagraph>(Assert.Single(SafeMarkdown.Parse("**Sahla** : zid `Supabase`")));

        Assert.Contains(paragraph.Inlines, i => i is MdStrong);
        Assert.Contains(paragraph.Inlines, i => i is MdCode { Code: "Supabase" });
    }

    [Fact]
    public void Html_is_shown_as_text_never_interpreted()
    {
        var blocks = SafeMarkdown.Parse("<script>alert(1)</script>\n\nwach <b>rak</b>");

        var text = string.Concat(blocks.OfType<MdParagraph>().Select(p => SafeMarkdown.ToPlainText(p.Inlines)));
        Assert.Contains("<script>alert(1)</script>", text);
        Assert.Contains("<b>rak</b>", text);
    }

    [Theory]
    [InlineData("[clic](javascript:alert(1))")]
    [InlineData("[fichier](file:///C:/Windows/System32/cmd.exe)")]
    [InlineData("[app](ms-settings:privacy)")]
    public void Unsafe_links_cannot_be_opened(string markdown)
    {
        var link = FindLink(markdown);

        Assert.Null(link.Url);
    }

    [Fact]
    public void Web_links_are_kept()
    {
        var link = FindLink("[docs](https://supabase.com/docs)");

        Assert.Equal("https://supabase.com/docs", link.Url!.ToString());
    }

    [Fact]
    public void Images_are_never_loaded()
    {
        var link = FindLink("![logo](https://tracker.example/pixel.png)");

        Assert.Equal("[image: logo]", SafeMarkdown.ToPlainText(link.Children));
    }

    [Fact]
    public void Code_blocks_keep_language_and_content()
    {
        var code = Assert.IsType<MdCodeBlock>(Assert.Single(SafeMarkdown.Parse("```csharp\nvar client = new Supabase.Client(url, key);\n```")));

        Assert.Equal("csharp", code.Language);
        Assert.Equal("var client = new Supabase.Client(url, key);", code.Code);
    }

    [Fact]
    public void Lists_headings_quotes_and_tables()
    {
        var blocks = SafeMarkdown.Parse("## Étapes\n\n3. zid le package\n4. initialisi\n\n> rod balek\n\n| a | b |\n|---|---|\n| 1 | 2 |");

        Assert.IsType<MdHeading>(blocks[0]);
        var list = Assert.IsType<MdList>(blocks[1]);
        Assert.True(list.Ordered);
        Assert.Equal(3, list.Start);
        Assert.Equal(2, list.Items.Count);
        Assert.IsType<MdQuote>(blocks[2]);
        var table = Assert.IsType<MdTable>(blocks[3]);
        Assert.True(table.HasHeader);
        Assert.Equal(2, table.Rows.Count);
    }

    [Fact]
    public void Arabic_text_is_preserved()
    {
        var paragraph = Assert.IsType<MdParagraph>(Assert.Single(SafeMarkdown.Parse("باش تconnecti، زيد `Supabase`.")));

        Assert.Equal("باش تconnecti، زيد Supabase.", SafeMarkdown.ToPlainText(paragraph.Inlines));
    }

    [Fact]
    public void Deep_nesting_does_not_overflow()
    {
        var markdown = string.Concat(Enumerable.Repeat("> ", 500)) + "text";

        Assert.NotEmpty(SafeMarkdown.Parse(markdown));
    }

    private static MdLink FindLink(string markdown)
    {
        var paragraph = Assert.IsType<MdParagraph>(Assert.Single(SafeMarkdown.Parse(markdown)));
        return Assert.IsType<MdLink>(Assert.Single(paragraph.Inlines, i => i is MdLink));
    }
}
