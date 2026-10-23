using ShoppyAI.Core.Text;

namespace ShoppyAI.Tests;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("sa7it", "sahit")]
    [InlineData("khouya", "khoya")]
    [InlineData("bezzaf", "bezaf")]
    [InlineData("qbel", "9bel")]
    [InlineData("wesh", "wech")]
    [InlineData("chkun", "chkoun")]
    [InlineData("mli7", "mlih")]
    [InlineData("WACH", "wach")]
    [InlineData("waaaach", "wach")]
    public void Spelling_variants_share_a_key(string a, string b)
    {
        Assert.Equal(TextNormalizer.DarijaKey(a), TextNormalizer.DarijaKey(b));
    }

    [Fact]
    public void Meaningful_double_vowels_are_kept()
    {
        Assert.NotEqual(TextNormalizer.DarijaKey("maa"), TextNormalizer.DarijaKey("ma"));
    }

    [Theory]
    [InlineData("أريد", "اريد")]
    [InlineData("مدرسة", "مدرسه")]
    [InlineData("بزااااف", "بزاف")]
    [InlineData("فَهِمْت", "فهمت")]
    [InlineData("ڨاع", "قاع")]
    [InlineData("مـــليح", "مليح")]
    public void Arabic_normalization(string input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.NormalizeArabic(input));
    }

    [Fact]
    public void Words_mixing_scripts_are_split()
    {
        Assert.Equal(["كيفاش", "ن", "connecti", "Supabase"], TextNormalizer.Tokenize("كيفاش نconnecti Supabase؟"));
    }

    [Fact]
    public void Code_and_urls_are_ignored()
    {
        var stripped = TextNormalizer.StripCodeAndUrls("wach `var x` https://a.b/c ```\ncode\n``` doka");

        Assert.DoesNotContain("var", stripped);
        Assert.DoesNotContain("https", stripped);
        Assert.DoesNotContain("code", stripped);
        Assert.Contains("doka", stripped);
    }
}

public class TextDirectionTests
{
    [Theory]
    [InlineData("واش راك؟", true)]
    [InlineData("wach rak ?", false)]
    [InlineData("WPF كيفاش نربطو مع Supabase؟", true)]
    [InlineData("kifach nconnecti Supabase maa WPF ?", false)]
    [InlineData("Le mot «واش» veut dire quoi ?", false)]
    [InlineData("", false)]
    [InlineData("123 456", false)]
    public void Direction_follows_the_dominant_script(string text, bool rtl)
    {
        Assert.Equal(rtl, TextDirection.IsRightToLeft(text));
    }
}
