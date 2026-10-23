using System.Text.Json;
using ShoppyAI.Core.Text;

namespace ShoppyAI.Core.Language;

public sealed record GlossaryEntry(
    string Term,
    IReadOnlyList<string> Forms,
    string Meaning,
    string? Note = null,
    string Dialect = "DZ");

/// <summary>
/// Darija / Arabizi glossary. Only entries whose forms appear in the conversation are
/// sent to the model, so the glossary can grow without filling the context window.
/// </summary>
public sealed class DarijaGlossary
{
    private readonly List<(GlossaryEntry Entry, string[] Keys)> _forms = [];

    public DarijaGlossary(IEnumerable<GlossaryEntry> entries)
    {
        Entries = entries.ToList();

        foreach (var entry in Entries)
        {
            foreach (var form in entry.Forms.Append(entry.Term).Distinct())
            {
                var keys = TextNormalizer.Tokenize(form).Select(TextNormalizer.DarijaKey).ToArray();
                if (keys.Length > 0)
                    _forms.Add((entry, keys));
            }
        }

        // Expressions first, so "wach rak" is preferred over "wach" + "rak".
        _forms.Sort((a, b) => b.Keys.Length.CompareTo(a.Keys.Length));
    }

    public IReadOnlyList<GlossaryEntry> Entries { get; }

    public static DarijaGlossary Parse(string json)
    {
        var file = JsonSerializer.Deserialize<GlossaryFile>(json, JsonOptions)
                   ?? throw new InvalidDataException("Glossary file is empty.");

        var entries = (file.Entries ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e.Term) && !string.IsNullOrWhiteSpace(e.Meaning))
            .Select(e => new GlossaryEntry(
                e.Term!.Trim(),
                (e.Forms ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList(),
                e.Meaning!.Trim(),
                string.IsNullOrWhiteSpace(e.Note) ? null : e.Note.Trim(),
                string.IsNullOrWhiteSpace(e.Dialect) ? "DZ" : e.Dialect.Trim()));

        return new DarijaGlossary(entries);
    }

    /// <summary>Returns a glossary where entries of <paramref name="overrides"/> replace entries with the same term.</summary>
    public DarijaGlossary MergeWith(DarijaGlossary overrides)
    {
        var replaced = overrides.Entries
            .Select(e => TextNormalizer.DarijaKey(e.Term))
            .ToHashSet(StringComparer.Ordinal);

        return new DarijaGlossary(Entries
            .Where(e => !replaced.Contains(TextNormalizer.DarijaKey(e.Term)))
            .Concat(overrides.Entries));
    }

    /// <summary>Entries whose forms occur in the given texts, in order of first appearance.</summary>
    public IReadOnlyList<GlossaryEntry> FindMatches(IEnumerable<string> texts, int maxEntries = 25)
    {
        var found = new List<GlossaryEntry>();

        foreach (var text in texts)
        {
            var positions = new List<HashSet<string>>();
            string? previousKey = null;

            foreach (var token in TextNormalizer.Tokenize(TextNormalizer.StripCodeAndUrls(text)))
            {
                var key = TextNormalizer.DarijaKey(token);
                var keys = KeysFor(key);
                foreach (var stem in TextNormalizer.NegationStems(key, previousKey))
                    keys.UnionWith(KeysFor(stem));

                positions.Add(keys);
                previousKey = key;
            }

            var covered = new bool[positions.Count];

            for (var i = 0; i < positions.Count; i++)
            {
                foreach (var (entry, keys) in _forms)
                {
                    if (found.Contains(entry) || i + keys.Length > positions.Count || covered[i])
                        continue;

                    var matches = true;
                    for (var j = 0; j < keys.Length && matches; j++)
                        matches = positions[i + j].Contains(keys[j]);

                    if (!matches)
                        continue;

                    if (keys.Length > 1)
                        Array.Fill(covered, true, i, keys.Length);

                    found.Add(entry);
                    if (found.Count >= maxEntries)
                        return found;
                }
            }
        }

        return found;
    }

    private static HashSet<string> KeysFor(string key) =>
        TextNormalizer.ContainsArabic(key)
            ? TextNormalizer.ArabicVariants(key).ToHashSet(StringComparer.Ordinal)
            : [key];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private sealed class GlossaryFile
    {
        public List<EntryDto>? Entries { get; set; }
    }

    private sealed class EntryDto
    {
        public string? Term { get; set; }
        public List<string>? Forms { get; set; }
        public string? Meaning { get; set; }
        public string? Note { get; set; }
        public string? Dialect { get; set; }
    }
}
