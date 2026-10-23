using System.Text.Json;
using System.Text.Json.Serialization;
using ShoppyAI.Core.Text;

namespace ShoppyAI.Core.Language;

/// <summary>A meaning confirmed by the user, e.g. "doka" = "maintenant".</summary>
public sealed record LanguageCorrection(
    string Id,
    string Term,
    string Meaning,
    string? Note,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Local, user-editable language corrections. Every change is journaled so it can be
/// undone, and disabling keeps the correction for later. Stored as JSON in the user's
/// data directory; nothing is sent anywhere except inside the local Ollama prompt.
/// </summary>
public sealed class CorrectionStore
{
    private const int MaxHistory = 50;

    private readonly string? _filePath;
    private readonly Lock _gate = new();
    private readonly List<LanguageCorrection> _corrections = [];
    private readonly List<HistoryEntry> _history = [];

    /// <param name="filePath">JSON file to persist to, or null for an in-memory store.</param>
    public CorrectionStore(string? filePath)
    {
        _filePath = filePath;
        Load();
    }

    public event EventHandler? Changed;

    /// <summary>Set when the file could not be read; the unreadable file is kept as a backup.</summary>
    public string? LoadError { get; private set; }

    public IReadOnlyList<LanguageCorrection> All
    {
        get
        {
            lock (_gate)
                return _corrections.ToList();
        }
    }

    public IReadOnlyList<LanguageCorrection> Active => All.Where(c => c.Enabled).ToList();

    public bool CanUndo
    {
        get
        {
            lock (_gate)
                return _history.Count > 0;
        }
    }

    public LanguageCorrection? Find(string idOrTerm)
    {
        lock (_gate)
            return FindUnlocked(idOrTerm);
    }

    /// <summary>Adds a correction, or updates the existing one for the same term.</summary>
    public LanguageCorrection AddOrUpdate(string term, string meaning, string? note = null)
    {
        if (string.IsNullOrWhiteSpace(term))
            throw new ArgumentException("The term is empty.", nameof(term));
        if (string.IsNullOrWhiteSpace(meaning))
            throw new ArgumentException("The meaning is empty.", nameof(meaning));

        var now = DateTimeOffset.Now;
        LanguageCorrection result;

        lock (_gate)
        {
            var existing = FindUnlocked(term);
            note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

            if (existing is null)
            {
                result = new LanguageCorrection(
                    Guid.NewGuid().ToString("N")[..8], term.Trim(), meaning.Trim(), note, true, now, now);
                _corrections.Add(result);
                Record("add", null, result);
            }
            else
            {
                result = existing with { Term = term.Trim(), Meaning = meaning.Trim(), Note = note, Enabled = true, UpdatedAt = now };
                Replace(existing, result);
                Record("update", existing, result);
            }

            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public bool SetEnabled(string idOrTerm, bool enabled)
    {
        lock (_gate)
        {
            var existing = FindUnlocked(idOrTerm);
            if (existing is null || existing.Enabled == enabled)
                return false;

            var updated = existing with { Enabled = enabled, UpdatedAt = DateTimeOffset.Now };
            Replace(existing, updated);
            Record(enabled ? "enable" : "disable", existing, updated);
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Remove(string idOrTerm)
    {
        lock (_gate)
        {
            var existing = FindUnlocked(idOrTerm);
            if (existing is null)
                return false;

            _corrections.Remove(existing);
            Record("remove", existing, null);
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Reverts the most recent change. Returns a short description, or null if there is nothing to undo.</summary>
    public string? Undo()
    {
        string description;

        lock (_gate)
        {
            if (_history.Count == 0)
                return null;

            var last = _history[^1];
            _history.RemoveAt(_history.Count - 1);

            var id = (last.After ?? last.Before)!.Id;
            var index = _corrections.FindIndex(c => c.Id == id);
            if (index >= 0)
                _corrections.RemoveAt(index);
            else
                index = _corrections.Count;

            if (last.Before is not null)
                _corrections.Insert(index, last.Before);

            var term = (last.Before ?? last.After)!.Term;
            description = $"{last.Action} \"{term}\"";
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return description;
    }

    private LanguageCorrection? FindUnlocked(string idOrTerm)
    {
        var trimmed = idOrTerm.Trim();
        var key = TextNormalizer.DarijaKey(trimmed);
        return _corrections.FirstOrDefault(c => c.Id == trimmed) ??
               _corrections.FirstOrDefault(c => TextNormalizer.DarijaKey(c.Term) == key);
    }

    private void Replace(LanguageCorrection existing, LanguageCorrection updated)
    {
        var index = _corrections.IndexOf(existing);
        _corrections[index] = updated;
    }

    private void Record(string action, LanguageCorrection? before, LanguageCorrection? after)
    {
        _history.Add(new HistoryEntry(action, before, after, DateTimeOffset.Now));
        if (_history.Count > MaxHistory)
            _history.RemoveAt(0);
    }

    private void Load()
    {
        if (_filePath is null || !File.Exists(_filePath))
            return;

        try
        {
            var file = JsonSerializer.Deserialize<CorrectionsFile>(File.ReadAllText(_filePath), JsonOptions);
            _corrections.AddRange(file?.Corrections ?? []);
            _history.AddRange(file?.History ?? []);
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            _corrections.Clear();
            _history.Clear();
            LoadError = error.Message;

            // Keep the unreadable file instead of overwriting it on the next save.
            var backup = $"{_filePath}.unreadable-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(_filePath, backup, overwrite: true);
            LoadError += $" (backup: {backup})";
        }
    }

    private void Save()
    {
        if (_filePath is null)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporary = _filePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new CorrectionsFile(1, _corrections, _history), JsonOptions));
        File.Move(temporary, _filePath, overwrite: true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed record HistoryEntry(
        string Action,
        LanguageCorrection? Before,
        LanguageCorrection? After,
        DateTimeOffset At);

    private sealed record CorrectionsFile(
        int Version,
        List<LanguageCorrection>? Corrections,
        List<HistoryEntry>? History);
}
