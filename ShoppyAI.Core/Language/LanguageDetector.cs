using ShoppyAI.Core.Text;

namespace ShoppyAI.Core.Language;

/// <summary>
/// Heuristic detection of Algerian Darija (Arabic script or Arabizi), Standard Arabic,
/// French and English. It only guides the reply language: the result is described to
/// the model as a hint that may be wrong, never as a certainty.
/// </summary>
public sealed class LanguageDetector
{
    private const string ArabiziDigits = "2356789";

    private static readonly string[] TechnicalPrefixes =
    [
        "python", "java", "net", "win", "windows", "php", "vue", "angular", "ubuntu", "debian", "macos", "ios",
        "android", "es", "ecma", "html", "css", "sql", "mysql", "postgres", "postgresql", "v", "version", "iphone",
        "rtx", "gtx", "ps", "xbox", "office", "sha", "md", "mp", "utf", "ipv", "http", "oauth", "web", "log", "py",
        "node", "react", "next", "qwen", "llama", "gemma", "phi", "gpt", "mistral", "deepseek", "tls", "ssl", "h"
    ];

    private static readonly string[] Units =
    [
        "px", "ms", "min", "go", "mo", "gb", "mb", "kb", "eme", "er", "ere", "th", "nd", "rd", "st", "fps", "hz",
        "ghz", "mhz", "km", "cm", "mm", "kg", "ans", "jours", "heures", "sec", "pm", "am"
    ];

    private readonly DialectMarkers _markers;

    public LanguageDetector(DialectMarkers markers)
    {
        _markers = markers;
    }

    public LanguageDetection Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return LanguageDetection.Empty;

        var tokens = TextNormalizer.Tokenize(TextNormalizer.StripCodeAndUrls(text));
        var hints = new List<VarietyHint>();

        double darijaArabic = 0, darijaLatin = 0, standardArabic = 0, french = 0, english = 0;
        int arabicWords = 0, latinWords = 0, arabiziWords = 0;
        string? previousKey = null;

        foreach (var token in tokens)
        {
            if (TextNormalizer.ContainsArabic(token))
            {
                var key = TextNormalizer.NormalizeArabic(token);
                if (key.Length >= 2)
                    arabicWords++;

                var variants = TextNormalizer.ArabicVariants(key).ToList();

                if (variants.Any(_markers.DarijaArabic.Contains) || IsArabicNegation(key, previousKey))
                    darijaArabic++;
                else if (variants.Any(_markers.StandardArabic.Contains))
                    standardArabic++;

                foreach (var (variety, words) in _markers.OtherVarietiesArabic)
                {
                    if (variants.Any(words.Contains))
                    {
                        hints.Add(new VarietyHint(variety, token));
                        darijaArabic++;
                    }
                }

                previousKey = key;
                continue;
            }

            if (!token.Any(char.IsLetter))
            {
                previousKey = null;
                continue;
            }

            latinWords++;
            var plain = TextNormalizer.PlainKey(token);
            var darijaKey = TextNormalizer.DarijaKey(token);
            var arabizi = IsArabiziWord(plain);

            if (arabizi)
                arabiziWords++;

            if (_markers.DarijaLatin.Contains(darijaKey) || arabizi || IsLatinNegation(darijaKey, previousKey))
                darijaLatin++;
            else if (_markers.DarijaLatinWeak.Contains(darijaKey))
                darijaLatin += 0.5;

            if (_markers.FrenchStrong.Contains(plain))
                french++;
            else if (_markers.FrenchWeak.Contains(plain))
                french += 0.5;

            if (_markers.EnglishStrong.Contains(plain))
                english++;
            else if (_markers.EnglishWeak.Contains(plain))
                english += 0.5;

            foreach (var (variety, words) in _markers.OtherVarietiesLatin)
            {
                if (words.Contains(darijaKey))
                {
                    hints.Add(new VarietyHint(variety, token));
                    darijaLatin++;
                }
            }

            previousKey = darijaKey;
        }

        var latinMarkerHits = darijaLatin + french + english;
        var script = (arabicWords, latinWords) switch
        {
            (0, 0) => WritingScript.None,
            (0, _) => WritingScript.Latin,
            (_, 0) => WritingScript.Arabic,
            // Unrecognized Latin words in an Arabic sentence are usually technical terms or names.
            _ => arabicWords >= latinMarkerHits ? WritingScript.Arabic : WritingScript.Latin
        };

        var darija = darijaArabic + darijaLatin;
        var language = script switch
        {
            WritingScript.Arabic when darija > 0 && darija >= standardArabic => DetectedLanguage.Darija,
            WritingScript.Arabic when standardArabic > darija => DetectedLanguage.StandardArabic,
            WritingScript.Arabic => DetectedLanguage.ArabicUnspecified,
            WritingScript.Latin when darija > 0 && darija >= french && darija >= english => DetectedLanguage.Darija,
            WritingScript.Latin when french > 0 && french >= english => DetectedLanguage.French,
            WritingScript.Latin when english > 0 => DetectedLanguage.English,
            _ => DetectedLanguage.Unknown
        };

        return new LanguageDetection(
            language,
            script,
            Confidence(language, darija, french, english, standardArabic),
            MixesScripts: arabicWords > 0 && latinWords > 0,
            UsesArabiziDigits: arabiziWords > 0,
            MixesFrench: language != DetectedLanguage.French && french >= 1,
            MixesEnglish: language != DetectedLanguage.English && english >= 1,
            OtherVarietyHints: hints,
            DarijaScore: darija,
            FrenchScore: french,
            EnglishScore: english,
            StandardArabicScore: standardArabic);
    }

    /// <summary>
    /// True when a Latin word uses digits as Arabic letters ("3lach", "m3a", "sa7it", "mli7"),
    /// and false for numbers and technical names ("3 fichiers", "mp3", "python3", "net10").
    /// </summary>
    public bool IsArabiziWord(string plainKey)
    {
        if (_markers.ArabiziExclusions.Contains(plainKey) ||
            !plainKey.Any(TextNormalizer.IsLatinLetter) ||
            !plainKey.Any(c => ArabiziDigits.Contains(c)))
        {
            return false;
        }

        var lastLetter = plainKey.ToList().FindLastIndex(char.IsLetter);
        var firstDigit = plainKey.ToList().FindIndex(char.IsDigit);

        if (firstDigit > lastLetter)
        {
            // Letters followed by digits: only a single trailing 3/7/9 is a letter ("sma3", "mli7").
            var letters = plainKey[..firstDigit];
            var digits = plainKey[firstDigit..];
            return digits.Length == 1 && "379".Contains(digits[0]) && letters.Length >= 2 &&
                   !TechnicalPrefixes.Contains(letters);
        }

        if (char.IsDigit(plainKey[0]))
        {
            var leadingDigits = plainKey.TakeWhile(char.IsDigit).Count();
            var rest = plainKey[leadingDigits..];
            return leadingDigits == 1 && rest.Length >= 2 && !Units.Contains(rest);
        }

        return true;
    }

    private static bool IsArabicNegation(string key, string? previousKey) =>
        (key.Length >= 5 && key.StartsWith("ما") && key.EndsWith('ش')) ||
        (previousKey == "ما" && key.Length >= 3 && key.EndsWith('ش'));

    private static bool IsLatinNegation(string darijaKey, string? previousKey) =>
        (darijaKey.Length >= 6 && darijaKey.StartsWith("ma") && darijaKey.EndsWith("ch")) ||
        (previousKey == "ma" && darijaKey.Length >= 4 && darijaKey.EndsWith("ch"));

    private static DetectionConfidence Confidence(
        DetectedLanguage language, double darija, double french, double english, double standardArabic)
    {
        var top = language switch
        {
            DetectedLanguage.Darija => darija,
            DetectedLanguage.French => french,
            DetectedLanguage.English => english,
            DetectedLanguage.StandardArabic => standardArabic,
            _ => 0
        };

        if (top <= 0)
            return DetectionConfidence.Low;

        var second = new[] { darija, french, english, standardArabic }
            .OrderDescending()
            .Skip(1)
            .First();

        return top >= 2 && top >= 2 * second ? DetectionConfidence.High : DetectionConfidence.Medium;
    }
}
