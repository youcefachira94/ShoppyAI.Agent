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
/// The file is re-read when another ShoppyAI process (console or desktop) changed it,
/// so the two never overwrite each other's corrections.
/// </summary>
public sealed class CorrectionStore
{
    private const int MaxHistory = 50;

    private readonly string? _filePath;
    private readonly Lock _gate = new();
    private List<LanguageCorrection> _corrections = [];
    private List<HistoryEntry> _history = [];
    private DateTime _loadedWriteTime;

    /// <summary>False when the file exists but could not be read: it is then never overwritten.</summary>
    private bool _persist;

    /// <param name="filePath">JSON file to persist to, or null for an in-memory store.</param>
    public CorrectionStore(string? filePath)
    {
        _filePath = filePath;
        _persist = filePath is not null;
        Load();
    }

    public event EventHandler? Changed;

    /// <summary>Set when the file could not be read; explains what happened to it.</summary>
    public string? LoadError { get; private set; }

    public IReadOnlyList<LanguageCorrection> All
    {
        get
        {
            lock (_gate)
            {
                RefreshIfChanged();
                return _corrections.ToList();
            }
        }
    }

    public IReadOnlyList<LanguageCorrection> Active => All.Where(c => c.Enabled).ToList();

    public bool CanUndo
    {
        get
        {
            lock (_gate)
            {
                RefreshIfChanged();
                return _history.Count > 0;
            }
        }
    }

    public LanguageCorrection? Find(string idOrTerm)
    {
        lock (_gate)
        {
            RefreshIfChanged();
            return FindUnlocked(idOrTerm);
        }
    }

    /// <summary>Adds a correction, or updates the existing one for the same term.</summary>
    /// <exception cref="IOException">The file could not be written; nothing was changed.</exception>
    public LanguageCorrection AddOrUpdate(string term, string meaning, string? note = null)
    {
        Validate(term, meaning);

        return Mutate(() =>
        {
            var existing = FindUnlocked(term);
            var now = DateTimeOffset.Now;

            if (existing is null)
            {
                var added = new LanguageCorrection(
                    Guid.NewGuid().ToString("N")[..8], term.Trim(), meaning.Trim(), Clean(note), true, now, now);
                _corrections.Add(added);
                Record("add", null, added);
                return added;
            }

            var updated = existing with { Term = term.Trim(), Meaning = meaning.Trim(), Note = Clean(note), Enabled = true, UpdatedAt = now };
            Replace(existing, updated);
            Record("update", existing, updated);
            return updated;
        });
    }

    /// <summary>Changes the correction with this id, including its word.</summary>
    /// <exception cref="ArgumentException">Unknown id, empty value, or the new word already has its own correction.</exception>
    public LanguageCorrection Update(string id, string term, string meaning, string? note)
    {
        Validate(term, meaning);

        return Mutate(() =>
        {
            var existing = _corrections.FirstOrDefault(c => c.Id == id)
                           ?? throw new ArgumentException("This correction no longer exists.", nameof(id));

            var key = TextNormalizer.DarijaKey(term.Trim());
            if (_corrections.Any(c => c.Id != id && TextNormalizer.DarijaKey(c.Term) == key))
                throw new ArgumentException($"A correction for \"{term.Trim()}\" already exists.", nameof(term));

            var updated = existing with { Term = term.Trim(), Meaning = meaning.Trim(), Note = Clean(note), UpdatedAt = DateTimeOffset.Now };
            Replace(existing, updated);
            Record("update", existing, updated);
            return updated;
        });
    }

    /// <returns>False when there is no such correction or it is already in that state.</returns>
    public bool SetEnabled(string idOrTerm, bool enabled) => Mutate(() =>
    {
        var existing = FindUnlocked(idOrTerm);
        if (existing is null || existing.Enabled == enabled)
            return false;

        var updated = existing with { Enabled = enabled, UpdatedAt = DateTimeOffset.Now };
        Replace(existing, updated);
        Record(enabled ? "enable" : "disable", existing, updated);
        return true;
    });

    public bool Remove(string idOrTerm) => Mutate(() =>
    {
        var existing = FindUnlocked(idOrTerm);
        if (existing is null)
            return false;

        _corrections.Remove(existing);
        Record("remove", existing, null);
        return true;
    });

    /// <summary>Reverts the most recent change. Returns a short description, or null if there is nothing to undo.</summary>
    public string? Undo() => Mutate(() =>
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

        return $"{last.Action} \"{(last.Before ?? last.After)!.Term}\"";
    });

    /// <summary>
    /// Applies a change and saves it. If saving fails, the change is rolled back and the
    /// error is rethrown, so memory and file never disagree.
    /// </summary>
    private T Mutate<T>(Func<T> change)
    {
        T result;

        lock (_gate)
        {
            RefreshIfChanged();
            var corrections = _corrections.ToList();
            var history = _history.ToList();

            try
            {
                result = change();
                Save();
            }
            catch
            {
                _corrections = corrections;
                _history = history;
                throw;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private static void Validate(string term, string meaning)
    {
        if (string.IsNullOrWhiteSpace(term))
            throw new ArgumentException("The term is empty.", nameof(term));
        if (string.IsNullOrWhiteSpace(meaning))
            throw new ArgumentException("The meaning is empty.", nameof(meaning));
    }

    private static string? Clean(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    private LanguageCorrection? FindUnlocked(string idOrTerm)
    {
        var trimmed = idOrTerm.Trim();
        var key = TextNormalizer.DarijaKey(trimmed);
        return _corrections.FirstOrDefault(c => c.Id == trimmed) ??
               _corrections.FirstOrDefault(c => TextNormalizer.DarijaKey(c.Term) == key);
    }

    private void Replace(LanguageCorrection existing, LanguageCorrection updated)
    {
        _corrections[_corrections.IndexOf(existing)] = updated;
    }

    private void Record(string action, LanguageCorrection? before, LanguageCorrection? after)
    {
        _history.Add(new HistoryEntry(action, before, after, DateTimeOffset.Now));
        if (_history.Count > MaxHistory)
            _history.RemoveAt(0);
    }

    private void Load()
    {
        if (_filePath is null)
            return;

        if (Directory.Exists(_filePath))
        {
            Disable($"{_filePath} is a folder, not a file.");
            return;
        }

        if (File.Exists(_filePath))
            ReadFile();
    }

    private void RefreshIfChanged()
    {
        if (_persist && File.Exists(_filePath) && File.GetLastWriteTimeUtc(_filePath!) != _loadedWriteTime)
            ReadFile();
    }

    private void ReadFile()
    {
        try
        {
            var writeTime = File.GetLastWriteTimeUtc(_filePath!);
            var file = JsonSerializer.Deserialize<CorrectionsFile>(File.ReadAllText(_filePath!), JsonOptions);
            _corrections = file?.Corrections ?? [];
            _history = file?.History ?? [];
            _loadedWriteTime = writeTime;
        }
        catch (JsonException error)
        {
            // Damaged file: keep it aside, start empty, and keep saving to a fresh file.
            var backup = $"{_filePath}.unreadable-{DateTime.Now:yyyyMMddHHmmss}";
            try
            {
                File.Move(_filePath!, backup, overwrite: true);
                LoadError = $"{error.Message} The damaged file was kept as {backup}.";
                _corrections = [];
                _history = [];
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                Disable($"{error.Message} {moveError.Message}");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The file may be fine (locked, no permission): never overwrite it.
            Disable(error.Message);
        }
    }

    private void Disable(string reason)
    {
        _persist = false;
        LoadError = $"{reason} Language corrections are kept in memory only until ShoppyAI restarts.";
    }

    private void Save()
    {
        if (!_persist)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporary = _filePath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new CorrectionsFile(1, _corrections, _history), JsonOptions));
            File.Move(temporary, _filePath!, overwrite: true);
            _loadedWriteTime = File.GetLastWriteTimeUtc(_filePath!);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
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
