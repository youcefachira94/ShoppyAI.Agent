namespace ShoppyAI.Core.Language;

/// <summary>Language the user asks ShoppyAI to answer in.</summary>
public enum ResponseLanguage
{
    /// <summary>Mirror the language and script of the user's latest message.</summary>
    Auto,
    DarijaLatin,
    DarijaArabic,
    French,
    English,
    StandardArabic
}

public enum DetectedLanguage
{
    Unknown,
    Darija,
    StandardArabic,
    /// <summary>Arabic script without clear Darija or Standard Arabic markers.</summary>
    ArabicUnspecified,
    French,
    English
}

public enum WritingScript
{
    None,
    Latin,
    Arabic
}

public enum DetectionConfidence
{
    Low,
    Medium,
    High
}

/// <summary>A word suggesting another Arabic variety than Algerian Darija.</summary>
public sealed record VarietyHint(string Variety, string Word);

/// <summary>Result of the heuristic language detection on one message.</summary>
public sealed record LanguageDetection(
    DetectedLanguage Language,
    WritingScript Script,
    DetectionConfidence Confidence,
    bool MixesScripts,
    bool UsesArabiziDigits,
    bool MixesFrench,
    bool MixesEnglish,
    IReadOnlyList<VarietyHint> OtherVarietyHints,
    double DarijaScore,
    double FrenchScore,
    double EnglishScore,
    double StandardArabicScore)
{
    public static LanguageDetection Empty { get; } = new(
        DetectedLanguage.Unknown, WritingScript.None, DetectionConfidence.Low,
        false, false, false, false, [], 0, 0, 0, 0);

    /// <summary>Score of the detected language.</summary>
    public double TopScore => Language switch
    {
        DetectedLanguage.Darija => DarijaScore,
        DetectedLanguage.French => FrenchScore,
        DetectedLanguage.English => EnglishScore,
        DetectedLanguage.StandardArabic => StandardArabicScore,
        _ => 0
    };

    /// <summary>
    /// True for short or ambiguous messages ("ok", "w la base de données ?") that should not
    /// switch the language of an ongoing conversation on their own.
    /// </summary>
    public bool IsWeak => Language == DetectedLanguage.Unknown ||
                          (Confidence != DetectionConfidence.High && TopScore < 2);

    /// <summary>Short English description, used in the system prompt.</summary>
    public string Describe()
    {
        var description = (Language, Script) switch
        {
            (DetectedLanguage.Darija, WritingScript.Arabic) => "Darija in Arabic script",
            (DetectedLanguage.Darija, _) => UsesArabiziDigits
                ? "Darija in Latin letters (Arabizi, with digits for Arabic letters)"
                : "Darija in Latin letters (Arabizi)",
            (DetectedLanguage.StandardArabic, _) => "Modern Standard Arabic",
            (DetectedLanguage.ArabicUnspecified, _) => "Arabic script (Darija or Standard Arabic, unclear)",
            (DetectedLanguage.French, _) => "French",
            (DetectedLanguage.English, _) => "English",
            _ => "unclear (too short or no clear language markers)"
        };

        var extras = new List<string>();
        if (MixesFrench)
            extras.Add("mixed with French");
        if (MixesEnglish)
            extras.Add("mixed with English");
        if (MixesScripts)
            extras.Add("with words in both Arabic and Latin letters");

        if (extras.Count > 0)
            description += ", " + string.Join(", ", extras);

        return description;
    }
}
