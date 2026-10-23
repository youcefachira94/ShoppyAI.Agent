using System.Text.Json;
using ShoppyAI.Core.Text;

namespace ShoppyAI.Core.Language;

/// <summary>Marker word lists used by <see cref="LanguageDetector"/>, loaded from dialect-markers.json.</summary>
public sealed class DialectMarkers
{
    public required IReadOnlySet<string> DarijaLatin { get; init; }

    /// <summary>Short connectors ("w" = and, "f" = in, "b" = with) that hint at Darija without proving it.</summary>
    public required IReadOnlySet<string> DarijaLatinWeak { get; init; }
    public required IReadOnlySet<string> DarijaArabic { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlySet<string>> OtherVarietiesLatin { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlySet<string>> OtherVarietiesArabic { get; init; }
    public required IReadOnlySet<string> StandardArabic { get; init; }
    public required IReadOnlySet<string> FrenchStrong { get; init; }
    public required IReadOnlySet<string> FrenchWeak { get; init; }
    public required IReadOnlySet<string> EnglishStrong { get; init; }
    public required IReadOnlySet<string> EnglishWeak { get; init; }
    public required IReadOnlySet<string> ArabiziExclusions { get; init; }

    public static DialectMarkers Parse(string json)
    {
        var file = JsonSerializer.Deserialize<MarkersFile>(json, JsonOptions)
                   ?? throw new InvalidDataException("dialect-markers.json is empty.");

        return new DialectMarkers
        {
            DarijaLatin = DarijaKeys(file.Darija?.Latin),
            DarijaLatinWeak = DarijaKeys(file.Darija?.WeakLatin),
            DarijaArabic = ArabicKeys(file.Darija?.Arabic),
            OtherVarietiesLatin = new Dictionary<string, IReadOnlySet<string>>
            {
                ["Moroccan"] = DarijaKeys(file.Moroccan?.Latin),
                ["Tunisian"] = DarijaKeys(file.Tunisian?.Latin),
                ["Egyptian/Levantine"] = DarijaKeys(file.MiddleEastern?.Latin)
            },
            OtherVarietiesArabic = new Dictionary<string, IReadOnlySet<string>>
            {
                ["Moroccan"] = ArabicKeys(file.Moroccan?.Arabic),
                ["Tunisian"] = ArabicKeys(file.Tunisian?.Arabic),
                ["Egyptian/Levantine"] = ArabicKeys(file.MiddleEastern?.Arabic)
            },
            StandardArabic = ArabicKeys(file.Msa?.Arabic),
            FrenchStrong = PlainKeys(file.French?.Strong),
            FrenchWeak = PlainKeys(file.French?.Weak),
            EnglishStrong = PlainKeys(file.English?.Strong),
            EnglishWeak = PlainKeys(file.English?.Weak),
            ArabiziExclusions = PlainKeys(file.ArabiziExclusions)
        };
    }

    private static HashSet<string> DarijaKeys(IEnumerable<string>? words) =>
        (words ?? []).Select(TextNormalizer.DarijaKey).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> ArabicKeys(IEnumerable<string>? words) =>
        (words ?? []).Select(TextNormalizer.NormalizeArabic).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> PlainKeys(IEnumerable<string>? words) =>
        (words ?? []).Select(TextNormalizer.PlainKey).ToHashSet(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private sealed class MarkersFile
    {
        public ScriptLists? Darija { get; set; }
        public ScriptLists? Moroccan { get; set; }
        public ScriptLists? Tunisian { get; set; }
        public ScriptLists? MiddleEastern { get; set; }
        public ScriptLists? Msa { get; set; }
        public StrengthLists? French { get; set; }
        public StrengthLists? English { get; set; }
        public List<string>? ArabiziExclusions { get; set; }
    }

    private sealed class ScriptLists
    {
        public List<string>? Latin { get; set; }
        public List<string>? WeakLatin { get; set; }
        public List<string>? Arabic { get; set; }
    }

    private sealed class StrengthLists
    {
        public List<string>? Strong { get; set; }
        public List<string>? Weak { get; set; }
    }
}
