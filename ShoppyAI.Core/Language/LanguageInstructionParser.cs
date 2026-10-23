using System.Text.Json;
using ShoppyAI.Core.Text;

namespace ShoppyAI.Core.Language;

/// <summary>
/// Recognizes requests about the reply language, alphabet or length in a user message
/// ("réponds en français", "من اليوم جاوبني بالعربية", "ktebli b l7rouf l3arbia",
/// "plus court stp"). It is deliberately conservative: a target language alone in a
/// normal sentence ("comment on dit bonjour en anglais ?", "tu parles arabe ?") is not
/// a request. Missed requests are still handled by the model, which is told to follow
/// an explicit request in the latest message.
/// </summary>
public sealed class LanguageInstructionParser
{
    private const int NegationWindow = 2;

    private readonly Dictionary<string, List<string[]>> _targets;
    private readonly List<string[]> _requests;
    private readonly List<string> _requestRoots;
    private readonly List<string[]> _durable;
    private readonly List<string[]> _once;
    private readonly List<string[]> _negations;
    private readonly List<string[]> _verbNegations;
    private readonly List<string[]> _politeness;
    private readonly HashSet<string> _prepositions;
    private readonly List<string[]> _notInstructions;
    private readonly List<string[]> _shorter;
    private readonly List<string[]> _longer;

    private LanguageInstructionParser(JsonElement root)
    {
        _targets = root.GetProperty("targets").EnumerateObject()
            .ToDictionary(p => p.Name, p => Phrases(p.Value));
        _requests = Phrases(root.GetProperty("requests"));
        _requestRoots = root.GetProperty("requestRoots").EnumerateArray().Select(e => Key(e.GetString()!)).ToList();
        _durable = Phrases(root.GetProperty("durable"));
        _once = Phrases(root.GetProperty("once"));
        _negations = Phrases(root.GetProperty("negations"));
        _verbNegations = Phrases(root.GetProperty("verbNegations"));
        _politeness = Phrases(root.GetProperty("politeness"));
        _prepositions = Phrases(root.GetProperty("prepositions")).Select(p => p[0]).ToHashSet(StringComparer.Ordinal);
        _notInstructions = Phrases(root.GetProperty("notInstructions"));
        _shorter = Phrases(root.GetProperty("shorter"));
        _longer = Phrases(root.GetProperty("longer"));
    }

    public static LanguageInstructionParser Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new LanguageInstructionParser(document.RootElement);
    }

    /// <returns>The request found in <paramref name="text"/>, or null when the message is not about the reply.</returns>
    public LanguageInstruction? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var positions = Positions(TextNormalizer.StripCodeAndUrls(text));
        if (positions.Count == 0 || Matches(positions, _notInstructions).Count > 0)
            return null;

        var covered = new bool[positions.Count];
        var targets = new List<(string Kind, int Start, int Length)>();
        foreach (var (kind, phrases) in _targets)
        {
            foreach (var match in Matches(positions, phrases))
                targets.Add((kind, match.Start, match.Length));
        }

        // Longest expressions win: "الحروف العربية" is an alphabet, not the word "Arabic".
        targets = KeepLongest(positions, targets, covered);

        var requests = Cover(Matches(positions, _requests), covered);
        var negatedVerbAt = NegatedRequestPosition(positions, requests);
        var negatedVerb = negatedVerbAt >= 0;
        var durable = Cover(Matches(positions, _durable), covered).Count > 0;
        var once = Cover(Matches(positions, _once), covered).Count > 0;
        var shorter = Cover(Matches(positions, _shorter), covered).Count > 0;
        var longer = Cover(Matches(positions, _longer), covered).Count > 0;
        var negations = Cover(Matches(positions, _negations), covered);
        Cover(Matches(positions, _verbNegations), covered);
        Cover(Matches(positions, _politeness), covered);

        for (var i = 0; i < positions.Count; i++)
        {
            if (positions[i].Overlaps(_prepositions))
                covered[i] = true;
        }

        var otherWords = covered.Count(c => !c);
        var isRequest = requests.Count > 0 || negatedVerb;
        // "cette fois en anglais : c'est quoi un rebase ?" is explicit even with a question after it.
        var aboutLanguage = targets.Count > 0 && (isRequest || once || otherWords <= 1);
        var aboutLength = (shorter || longer) && (isRequest || otherWords <= 2);

        if (!aboutLanguage && !aboutLength)
            return null;

        ReplyLanguage? language = null;
        ReplyScript? script = null;
        ReplyLanguage? avoid = null;

        if (aboutLanguage)
        {
            // A negated verb ("ne réponds pas", "ma tjawebnich") negates the first language after it only:
            // "ne réponds pas en darija, en français stp" asks for French.
            var firstAfterNegatedVerb = negatedVerb
                ? targets.Where(t => t.Start > negatedVerbAt).Select(t => t.Start).DefaultIfEmpty(-1).Min()
                : -1;

            foreach (var (kind, start, _) in targets.OrderBy(t => t.Start))
            {
                var negated = start == firstAfterNegatedVerb ||
                              negations.Any(n => n.Start + n.Length <= start && start - (n.Start + n.Length) <= NegationWindow - 1);
                var (targetLanguage, targetScript) = Meaning(kind);

                if (negated)
                {
                    if (targetLanguage is { } unwanted && unwanted != ReplyLanguage.Auto)
                        avoid = unwanted;
                    continue;
                }

                if (targetLanguage is not null && !(language == ReplyLanguage.StandardArabic && kind == "arabic"))
                    language = targetLanguage;
                if (targetScript is not null)
                    script = targetScript;
            }

            if (language is not null)
                avoid = avoid == language ? null : avoid;
        }

        AnswerLength? length = shorter ? AnswerLength.Short : longer ? AnswerLength.Detailed : null;
        var preference = new ReplyPreference(language, script, length);
        if (preference.IsEmpty && avoid is null)
            return null;

        var scope = once ? PreferenceScope.Turn : durable ? PreferenceScope.Persistent : PreferenceScope.Conversation;
        return new LanguageInstruction(preference, scope, language is null ? avoid : null);
    }

    private static (ReplyLanguage? Language, ReplyScript? Script) Meaning(string kind) => kind switch
    {
        "darija" => (ReplyLanguage.Darija, null),
        "standardArabic" => (ReplyLanguage.StandardArabic, ReplyScript.Arabic),
        // "بالعربية" / "en arabe": Arabic letters, in Algerian Darija unless Standard Arabic is named.
        "arabic" => (ReplyLanguage.Darija, ReplyScript.Arabic),
        "french" => (ReplyLanguage.French, null),
        "english" => (ReplyLanguage.English, null),
        "arabicScript" => (null, ReplyScript.Arabic),
        "latinScript" => (null, ReplyScript.Latin),
        "mirror" => (ReplyLanguage.Auto, ReplyScript.Auto),
        _ => (null, null)
    };

    /// <returns>Position of a negated request verb, or -1.</returns>
    private int NegatedRequestPosition(List<HashSet<string>> positions, List<(int Start, int Length)> requests)
    {
        var verbNegations = Matches(positions, _verbNegations);
        foreach (var request in requests)
        {
            if (verbNegations.Any(n => n.Start + n.Length == request.Start))
                return request.Start;
        }

        // "ma tjawebnich", "matjawebnich", "ما تجاوبنيش": the negation wraps the verb.
        for (var i = 0; i < positions.Count; i++)
        {
            var afterMa = i > 0 && (positions[i - 1].Contains("ma") || positions[i - 1].Contains("ما"));
            foreach (var key in positions[i])
            {
                var wrapped = key.EndsWith("ch") && (afterMa || key.StartsWith("ma")) ||
                              key.EndsWith('ش') && (afterMa || key.StartsWith('م'));
                if (wrapped && _requestRoots.Any(root => key.Contains(root)))
                    return i;
            }
        }

        return -1;
    }

    private static List<(string Kind, int Start, int Length)> KeepLongest(
        List<HashSet<string>> positions, List<(string Kind, int Start, int Length)> targets, bool[] covered)
    {
        var kept = new List<(string Kind, int Start, int Length)>();
        foreach (var target in targets.OrderByDescending(t => t.Length))
        {
            if (Enumerable.Range(target.Start, target.Length).Any(i => covered[i]))
                continue;

            Array.Fill(covered, true, target.Start, target.Length);
            kept.Add(target);
        }

        return kept;
    }

    private static List<(int Start, int Length)> Cover(List<(int Start, int Length)> matches, bool[] covered)
    {
        foreach (var (start, length) in matches)
            Array.Fill(covered, true, start, length);

        return matches;
    }

    private static List<(int Start, int Length)> Matches(List<HashSet<string>> positions, List<string[]> phrases)
    {
        var found = new List<(int Start, int Length)>();
        for (var i = 0; i < positions.Count; i++)
        {
            foreach (var phrase in phrases)
            {
                if (i + phrase.Length > positions.Count)
                    continue;

                var matches = true;
                for (var j = 0; j < phrase.Length && matches; j++)
                    matches = positions[i + j].Contains(phrase[j]);

                if (matches)
                    found.Add((i, phrase.Length));
            }
        }

        return found;
    }

    /// <summary>For each word of the message, the keys it can match (with or without attached prefixes).</summary>
    private static List<HashSet<string>> Positions(string text) =>
        TextNormalizer.Tokenize(text).Select(Variants).ToList();

    private static HashSet<string> Variants(string token)
    {
        var key = Key(token);
        var variants = new HashSet<string>(StringComparer.Ordinal) { key };

        if (TextNormalizer.ContainsArabic(key))
        {
            // و + (ب | ف | ل | ك) + ال + word, e.g. "وبالعربية" -> "عربيه".
            var rest = key;
            if (rest.StartsWith('و') && rest.Length > 3)
                variants.Add(rest = rest[1..]);
            if (rest.Length > 3 && "بفلك".Contains(rest[0]))
                variants.Add(rest = rest[1..]);
            if (rest.StartsWith("ال") && rest.Length > 3)
                variants.Add(rest[2..]);
        }
        else
        {
            // Darija prepositions glued to the word: "bdarija", "l3arbia", "bel3arbia".
            foreach (var prefix in new[] { "bel", "bl", "el", "b", "l" })
            {
                if (key.StartsWith(prefix) && key.Length - prefix.Length >= 4)
                    variants.Add(key[prefix.Length..]);
            }
        }

        return variants;
    }

    private static string Key(string word) =>
        TextNormalizer.ContainsArabic(word) ? TextNormalizer.NormalizeArabic(word) : TextNormalizer.PlainKey(word);

    private static List<string[]> Phrases(JsonElement array) => array.EnumerateArray()
        .Select(e => TextNormalizer.Tokenize(e.GetString()!).Select(Key).ToArray())
        .Where(p => p.Length > 0)
        .ToList();
}
