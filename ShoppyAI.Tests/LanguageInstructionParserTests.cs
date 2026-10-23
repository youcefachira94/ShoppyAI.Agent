using ShoppyAI.Core.Language;

namespace ShoppyAI.Tests;

public class LanguageInstructionParserTests
{
    private static LanguageInstruction? Find(string text) => TestData.Resources.Instructions.Find(text);

    [Theory]
    [InlineData("réponds en français", ReplyLanguage.French, null, PreferenceScope.Conversation)]
    [InlineData("Réponds-moi en anglais stp", ReplyLanguage.English, null, PreferenceScope.Conversation)]
    [InlineData("en français stp", ReplyLanguage.French, null, PreferenceScope.Conversation)]
    [InlineData("français", ReplyLanguage.French, null, PreferenceScope.Conversation)]
    [InlineData("in English please", ReplyLanguage.English, null, PreferenceScope.Conversation)]
    [InlineData("answer in English from now on", ReplyLanguage.English, null, PreferenceScope.Persistent)]
    [InlineData("من اليوم جاوبني بالعربية", ReplyLanguage.Darija, ReplyScript.Arabic, PreferenceScope.Persistent)]
    [InlineData("جاوبني بالحروف العربية من الآن", null, ReplyScript.Arabic, PreferenceScope.Persistent)]
    [InlineData("jawbni b darija", ReplyLanguage.Darija, null, PreferenceScope.Conversation)]
    [InlineData("ktebli b l7rouf l3arbia", null, ReplyScript.Arabic, PreferenceScope.Conversation)]
    [InlineData("ekteb b arabizi", null, ReplyScript.Latin, PreferenceScope.Conversation)]
    [InlineData("dima b l3arbia", ReplyLanguage.Darija, ReplyScript.Arabic, PreferenceScope.Persistent)]
    [InlineData("machi b lfusha, b darija", ReplyLanguage.Darija, null, PreferenceScope.Conversation)]
    [InlineData("la, b l3arbia machi b latin", ReplyLanguage.Darija, ReplyScript.Arabic, PreferenceScope.Conversation)]
    [InlineData("pas en anglais, en darija stp", ReplyLanguage.Darija, null, PreferenceScope.Conversation)]
    [InlineData("juste cette fois en anglais", ReplyLanguage.English, null, PreferenceScope.Turn)]
    [InlineData("réponds dans ma langue", ReplyLanguage.Auto, ReplyScript.Auto, PreferenceScope.Conversation)]
    [InlineData("désormais réponds dans ma langue", ReplyLanguage.Auto, ReplyScript.Auto, PreferenceScope.Persistent)]
    [InlineData("جاوبني بالفصحى", ReplyLanguage.StandardArabic, ReplyScript.Arabic, PreferenceScope.Conversation)]
    [InlineData("hder m3aya b darija bla français", ReplyLanguage.Darija, null, PreferenceScope.Conversation)]
    public void Recognizes_reply_language_requests(string text, ReplyLanguage? language, ReplyScript? script, PreferenceScope scope)
    {
        var instruction = Find(text);

        Assert.NotNull(instruction);
        Assert.Equal(language, instruction.Preference.Language);
        Assert.Equal(script, instruction.Preference.Script);
        Assert.Equal(scope, instruction.Scope);
    }

    [Theory]
    [InlineData("ne réponds pas en anglais")]
    [InlineData("don't answer in English")]
    [InlineData("ما تجاوبنيش بالفرنسية")]
    [InlineData("ma tjawebnich b français")]
    public void Negative_requests_only_exclude_a_language(string text)
    {
        var instruction = Find(text);

        Assert.NotNull(instruction);
        Assert.Null(instruction.Preference.Language);
        Assert.NotNull(instruction.Avoid);
    }

    [Theory]
    [InlineData("plus court stp", AnswerLength.Short)]
    [InlineData("jawbni b9sar", AnswerLength.Short)]
    [InlineData("trop long", AnswerLength.Short)]
    [InlineData("réponds plus en détail", AnswerLength.Detailed)]
    public void Recognizes_answer_length_requests(string text, AnswerLength expected)
    {
        Assert.Equal(expected, Find(text)?.Preference.Length);
    }

    [Fact]
    public void Language_and_length_together()
    {
        var instruction = Find("toujours en darija et plus court");

        Assert.Equal(new ReplyPreference(ReplyLanguage.Darija, null, AnswerLength.Short), instruction?.Preference);
        Assert.Equal(PreferenceScope.Persistent, instruction?.Scope);
    }

    [Theory]
    [InlineData("comment on dit bonjour en anglais ?")]
    [InlineData("tu parles arabe ?")]
    [InlineData("traduis ce texte en français")]
    [InlineData("je veux apprendre l'anglais")]
    [InlineData("c'est toujours en anglais les erreurs")]
    [InlineData("le français est difficile")]
    [InlineData("wach rak khoya")]
    [InlineData("oui")]
    [InlineData("vasy")]
    [InlineData("saha")]
    [InlineData("kifach nconnecti Supabase maa WPF ?")]
    [InlineData("utilise des noms de variables en anglais")]
    [InlineData("comment répondre en anglais à un client ?")]
    [InlineData("what can you do for me?")]
    [InlineData("écris le message en anglais")]
    [InlineData("donne-moi un code court en C#")]
    public void Ordinary_messages_are_not_requests(string text)
    {
        Assert.Null(Find(text));
    }
}
