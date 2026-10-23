using ShoppyAI.Core.Language;

namespace ShoppyAI.Tests;

public class LanguageDetectorTests
{
    private static LanguageDetection Detect(string text) => TestData.Detector.Detect(text);

    [Theory]
    [InlineData("wach rak dir ?")]
    [InlineData("rani hab ndir application")]
    [InlineData("3lach ma ykhdemch ?")]
    [InlineData("9oli wach ndir doka")]
    [InlineData("kifach nconnecti Supabase maa WPF ?")]
    [InlineData("ma fhemtch, 3awed fassarli")]
    public void Mission_examples_are_darija_in_latin_letters(string text)
    {
        var detection = Detect(text);

        Assert.Equal(DetectedLanguage.Darija, detection.Language);
        Assert.Equal(WritingScript.Latin, detection.Script);
    }

    [Theory]
    [InlineData("3lach ma ykhdemch ?")]
    [InlineData("9oli wach ndir doka")]
    [InlineData("ma fhemtch, 3awed fassarli")]
    public void Arabizi_digits_are_recognized(string text)
    {
        Assert.True(Detect(text).UsesArabiziDigits);
    }

    [Theory]
    [InlineData("واش راك دير؟")]
    [InlineData("علاش البرنامج ما يخدمش؟")]
    [InlineData("راني حاب ندير تطبيق")]
    [InlineData("مافهمتش، عاود فسرلي")]
    public void Darija_in_arabic_script(string text)
    {
        var detection = Detect(text);

        Assert.Equal(DetectedLanguage.Darija, detection.Language);
        Assert.Equal(WritingScript.Arabic, detection.Script);
    }

    [Fact]
    public void Arabic_sentence_with_latin_technical_terms_stays_arabic_script()
    {
        var detection = Detect("كيفاش نconnecti Supabase مع WPF؟");

        Assert.Equal(DetectedLanguage.Darija, detection.Language);
        Assert.Equal(WritingScript.Arabic, detection.Script);
        Assert.True(detection.MixesScripts);
    }

    [Fact]
    public void Modern_standard_arabic_is_not_taken_for_darija()
    {
        var detection = Detect("ماذا يجب أن أفعل لتثبيت البرنامج؟");

        Assert.Equal(DetectedLanguage.StandardArabic, detection.Language);
    }

    [Theory]
    [InlineData("Bonjour, comment je peux installer Ollama ?", DetectedLanguage.French)]
    [InlineData("j'ai 3 fichiers qui ne compilent pas", DetectedLanguage.French)]
    [InlineData("How do I connect Supabase with WPF?", DetectedLanguage.English)]
    public void French_and_english(string text, DetectedLanguage expected)
    {
        var detection = Detect(text);

        Assert.Equal(expected, detection.Language);
        Assert.False(detection.UsesArabiziDigits);
    }

    [Fact]
    public void Darija_mixed_with_french_is_flagged()
    {
        var detection = Detect("c'est bon, bessah 3lach le build ma ykhdemch ?");

        Assert.Equal(DetectedLanguage.Darija, detection.Language);
        Assert.True(detection.MixesFrench);
    }

    [Fact]
    public void Moroccan_words_are_reported_without_changing_the_language()
    {
        var detection = Detect("chno bghiti ndir daba ?");

        Assert.Equal(DetectedLanguage.Darija, detection.Language);
        Assert.Contains(detection.OtherVarietyHints, h => h.Variety == "Moroccan" && h.Word == "daba");
    }

    [Theory]
    [InlineData("3lach", true)]
    [InlineData("m3a", true)]
    [InlineData("sa7it", true)]
    [InlineData("mli7", true)]
    [InlineData("sma3", true)]
    [InlineData("9oli", true)]
    [InlineData("3", false)]
    [InlineData("mp3", false)]
    [InlineData("python3", false)]
    [InlineData("net10", false)]
    [InlineData("2eme", false)]
    [InlineData("3d", false)]
    [InlineData("10px", false)]
    public void Arabizi_words_versus_numbers_and_technical_names(string word, bool expected)
    {
        Assert.Equal(expected, TestData.Detector.IsArabiziWord(word));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("```\nvar x = 3;\n```")]
    public void Empty_or_code_only_is_unknown(string text)
    {
        Assert.Equal(DetectedLanguage.Unknown, Detect(text).Language);
    }

    [Fact]
    public void Description_mentions_arabizi_and_mixing()
    {
        var description = Detect("c'est bon, bessah 3lach le build ma ykhdemch ?").Describe();

        Assert.Contains("Latin letters", description);
        Assert.Contains("mixed with French", description);
    }
}
