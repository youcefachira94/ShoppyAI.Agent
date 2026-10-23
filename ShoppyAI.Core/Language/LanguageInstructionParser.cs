using System.Text.Json;
using ShoppyAI.Core.Text;

namespace ShoppyAI.Core.Language;

/// <summary>
/// Recognizes requests about the reply language, alphabet or length in a user message
/// ("réponds en français", "من اليوم جاوبني بالعربية", "ktebli b l7rouf l3arbia",
/// "plus court stp"). It is deliberately conservative: a request must be addressed to the
/// assistant ("réponds", "can you speak…", "jawbni"), so complaints and descriptions
/// ("pourquoi tu réponds toujours en anglais ?", "mon API répond en anglais") and ordinary
/// sentences ("comment on dit bonjour en anglais ?", "clavier arabe ?") are not requests.
/// Missed requests are still handled by the model, which is told to follow an explicit
/// request in the latest message.
/// </summary>
public sealed class LanguageInstructionParser
{
    private readonly Dictionary<string, List<string[]>> _targets;
    private readonly List<string[]> _requests;
    private readonly List<string[]> _speakVerbs;
    private readonly List<string[]> _infinitives;
    private readonly List<string> _requestRoots;
    private readonly List<string[]> _durable;
    private readonly List<string[]> _once;
    private readonly List<string[]> _negations;
    private readonly List<string[]> _verbNegations;
    private readonly List<string[]> _politeness;
    private readonly List<string[]> _notInstructions;
    private readonly List<string[]> _shorter;
    private readonly List<string[]> _longer;
    private readonly HashSet<string> _prepositions;
    private readonly HashSet<string> _connectors;
    private readonly HashSet<string> _modals;
    private readonly HashSet<string> _firstPerson;
    private readonly HashSet<string> _subjects;
    private readonly HashSet<string> _modalsBeforeYou;
    private readonly HashSet<string> _toWords;
    private readonly HashSet<string> _allowedBeforeTo;
    private readonly HashSet<string> _adverbs;
    private readonly HashSet<string> _fillers;
    private readonly HashSet<string> _why;
    private readonly HashSet<string> _learning;

    private LanguageInstructionParser(JsonElement root)
    {
        _targets = root.GetProperty("targets").EnumerateObject()
            .ToDictionary(p => p.Name, p => Phrases(p.Value));
        _requests = Phrases(root.GetProperty("requests"));
        _speakVerbs = Phrases(root.GetProperty("speakVerbs"));
        _infinitives = Phrases(root.GetProperty("infinitives"));
        _requestRoots = root.GetProperty("requestRoots").EnumerateArray().Select(e => Key(e.GetString()!)).ToList();
        _durable = Phrases(root.GetProperty("durable"));
        _once = Phrases(root.GetProperty("once"));
        _negations = Phrases(root.GetProperty("negations"));
        _verbNegations = Phrases(root.GetProperty("verbNegations"));
        _politeness = Phrases(root.GetProperty("politeness"));
        _notInstructions = Phrases(root.GetProperty("notInstructions"));
        _shorter = Phrases(root.GetProperty("shorter"));
        _longer = Phrases(root.GetProperty("longer"));
        _prepositions = Words(root, "prepositions");
        _connectors = Words(root, "connectors");
        _modals = Words(root, "modals");
        _firstPerson = Words(root, "firstPerson");
        _subjects = Words(root, "subjects");
        _modalsBeforeYou = Words(root, "modalsBeforeYou");
        _toWords = Words(root, "toWords");
        _allowedBeforeTo = Words(root, "allowedBeforeTo");
        _adverbs = Words(root, "adverbs");
        _fillers = Words(root, "fillers");
        _why = Words(root, "why");
        _learning = Words(root, "learning");
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

        // Translations ("traduis…", "comment on dit…") and questions about the past behaviour
        // ("pourquoi tu réponds en anglais ?") are not requests.
        if (positions.Count == 0 || Matches(positions, _notInstructions).Count > 0 || positions.Any(p => p.Overlaps(_why)))
            return null;

        var covered = new bool[positions.Count];
        var targets = new List<(string Kind, int Start, int Length)>();
        foreach (var (kind, phrases) in _targets)
        {
            foreach (var match in Matches(positions, phrases))
                targets.Add((kind, match.Start, match.Length));
        }

        // Longest expressions win: "الحروف العربية" is an alphabet, not the word "Arabic".
        targets = KeepLongest(targets, covered);

        // Learning a language is not choosing the reply language: "apprendre l'anglais", "cours d'anglais".
        if (targets.Any(t => PreviousWord(positions, t.Start) is { } before && positions[before].Overlaps(_learning)))
            return null;

        // Words of "from now on", "s'il te plaît"… are skipped when looking for the subject of a verb.
        var phraseWords = new bool[positions.Count];
        var durable = Cover(Matches(positions, _durable), covered, phraseWords).Count > 0;
        var once = Cover(Matches(positions, _once), covered, phraseWords).Count > 0;
        Cover(Matches(positions, _politeness), covered, phraseWords);

        var requests = Cover(FindRequests(positions, targets, phraseWords), covered);
        var negatedVerbAt = NegatedRequestPosition(positions, requests);
        var negatedVerb = negatedVerbAt >= 0;
        var shorter = Cover(Matches(positions, _shorter), covered).Count > 0;
        var longer = Cover(Matches(positions, _longer), covered).Count > 0;
        var negations = Cover(Matches(positions, _negations), covered);
        Cover(Matches(positions, _verbNegations), covered);

        for (var i = 0; i < positions.Count; i++)
        {
            // A leading "et / and / w" is a follow-up question ("et en arabe ?"), not a request.
            if (positions[i].Overlaps(_prepositions) || i > 0 && positions[i].Overlaps(_connectors))
                covered[i] = true;
        }

        // Without a request verb, the message must be nothing but the request ("en français stp",
        // "dima b l3arbia", "plus court"): "clavier arabe ?" or "path too long" are not requests.
        var onlyTheRequest = covered.All(c => c);
        var isRequest = requests.Count > 0 || negatedVerb;
        // "cette fois en anglais : c'est quoi un rebase ?" is explicit even with a question after it.
        var aboutLanguage = targets.Count > 0 && (isRequest || once || onlyTheRequest);
        var aboutLength = (shorter || longer) && (isRequest || onlyTheRequest);

        if (!aboutLanguage && !aboutLength)
            return null;

        ReplyLanguage? language = null;
        ReplyScript? script = null;
        ReplyLanguage? avoid = null;

        if (aboutLanguage)
        {
            // A negated verb ("ne réponds pas", "never answer", "لا تجاوبني") negates the first language after it
            // only: "ne réponds pas en darija, en français stp" asks for French.
            var firstAfterNegatedVerb = negatedVerb
                ? targets.Where(t => t.Start > negatedVerbAt).Select(t => t.Start).DefaultIfEmpty(-1).Min()
                : -1;

            foreach (var (kind, start, _) in targets.OrderBy(t => t.Start))
            {
                var negated = start == firstAfterNegatedVerb ||
                              negations.Any(n => n.Start + n.Length <= start && WordsBetween(positions, n.Start + n.Length, start) == 0);
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

        // Only languages, alphabets and lengths are saved; "never answer in English" lasts for the conversation.
        if (preference.IsEmpty && scope == PreferenceScope.Persistent)
            scope = PreferenceScope.Conversation;

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

    /// <summary>
    /// Request verbs addressed to the assistant: imperatives ("réponds", "jawbni"), "parle / speak / talk"
    /// directly followed by a language, and infinitives after "peux / pourrais / merci de".
    /// </summary>
    private List<(int Start, int Length)> FindRequests(
        List<HashSet<string>> positions, List<(string Kind, int Start, int Length)> targets, bool[] phraseWords)
    {
        var requests = Matches(positions, _requests)
            .Where(r => AddressedToAssistant(positions, r.Start, phraseWords))
            .ToList();

        foreach (var verb in Matches(positions, _speakVerbs))
        {
            var next = verb.Start + verb.Length;
            while (next < positions.Count && (positions[next].Overlaps(_prepositions) || positions[next].Overlaps(_fillers)))
                next++;

            if (targets.Any(t => t.Start == next) && AddressedToAssistant(positions, verb.Start, phraseWords))
                requests.Add(verb);
        }

        foreach (var verb in Matches(positions, _infinitives))
        {
            for (var i = Math.Max(0, verb.Start - 3); i < verb.Start; i++)
            {
                // "tu peux répondre…" is a request, "je peux répondre…" is not.
                if (positions[i].Overlaps(_modals) && !(i > 0 && positions[i - 1].Overlaps(_firstPerson)))
                {
                    requests.Add(verb);
                    break;
                }
            }
        }

        return requests.Distinct().ToList();
    }

    /// <summary>
    /// False when the verb has a subject ("tu réponds toujours…", "you always answer…") or depends on
    /// another verb ("need to reply", "apprendre à parler"); "can you answer…", "want you to reply…" are fine.
    /// </summary>
    private bool AddressedToAssistant(List<HashSet<string>> positions, int verbStart, bool[] phraseWords)
    {
        var i = verbStart - 1;
        while (i >= 0 && (positions[i].Overlaps(_adverbs) || phraseWords[i]))
            i--;

        if (i < 0)
            return true;

        if (positions[i].Overlaps(_toWords))
            return i > 0 && positions[i - 1].Overlaps(_allowedBeforeTo);
        if (positions[i].Contains("you"))
            return i > 0 && positions[i - 1].Overlaps(_modalsBeforeYou);

        return !positions[i].Overlaps(_subjects);
    }

    /// <returns>Position of a negated request verb, or -1.</returns>
    private int NegatedRequestPosition(List<HashSet<string>> positions, List<(int Start, int Length)> requests)
    {
        var verbNegations = Matches(positions, _verbNegations);
        foreach (var request in requests)
        {
            if (verbNegations.Any(n => n.Start + n.Length == request.Start))
                return request.Start;
        }

        // "ma tjawebnich", "matjawebnich", "ما تجاوبنيش", "لا تجاوبني": the negation is around or before the verb.
        for (var i = 0; i < positions.Count; i++)
        {
            var afterMa = i > 0 && (positions[i - 1].Contains("ma") || positions[i - 1].Contains("ما"));
            var afterLa = i > 0 && positions[i - 1].Contains("لا");
            foreach (var key in positions[i])
            {
                var negated = key.EndsWith("ch") && (afterMa || key.StartsWith("ma")) ||
                              key.EndsWith('ش') && (afterMa || key.StartsWith('م')) ||
                              afterLa && key.StartsWith('ت');
                if (negated && _requestRoots.Any(root => key.Contains(root)))
                    return i;
            }
        }

        return -1;
    }

    /// <summary>Index of the previous word that is not a preposition, or null.</summary>
    private int? PreviousWord(List<HashSet<string>> positions, int start)
    {
        for (var i = start - 1; i >= 0; i--)
        {
            if (!positions[i].Overlaps(_prepositions))
                return i;
        }

        return null;
    }

    /// <summary>Number of words between two positions, prepositions not counted ("machi b l'anglais" = 0).</summary>
    private int WordsBetween(List<HashSet<string>> positions, int from, int to) =>
        Enumerable.Range(from, Math.Max(0, to - from)).Count(i => !positions[i].Overlaps(_prepositions));

    private static List<(string Kind, int Start, int Length)> KeepLongest(
        List<(string Kind, int Start, int Length)> targets, bool[] covered)
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

    private static List<(int Start, int Length)> Cover(List<(int Start, int Length)> matches, params bool[][] flags)
    {
        foreach (var (start, length) in matches)
        {
            foreach (var flag in flags)
                Array.Fill(flag, true, start, length);
        }

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

    private static HashSet<string> Words(JsonElement root, string name) =>
        Phrases(root.GetProperty(name)).Select(p => p[0]).ToHashSet(StringComparer.Ordinal);
}
