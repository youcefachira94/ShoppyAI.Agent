using ShoppyAI.Core.Settings;

namespace ShoppyAI.Core.Language;

/// <summary>
/// The user's saved reply preferences: language, Darija alphabet, answer length and free
/// style notes. With <see cref="Remember"/> on they are kept in settings.json for new
/// conversations; otherwise they last until ShoppyAI is closed.
/// </summary>
public sealed class LanguagePreferences
{
    private const int MaxStyleNotes = 20;

    private readonly SettingsStore? _store;
    private PreferenceSettings _memory = new();

    /// <summary>A change could not be written: keep it in memory instead of re-reading the file.</summary>
    private bool _unsaved;

    /// <param name="store">Settings file, or null to keep everything in memory (tests, evaluation).</param>
    public LanguagePreferences(SettingsStore? store)
    {
        _store = store;
        if (store is not null)
            _memory = Copy(store.Settings.Preferences);
    }

    public event EventHandler? Changed;

    public bool Remember => Current.Remember;

    /// <summary>Saved language, alphabet and length; empty fields mean Auto.</summary>
    public ReplyPreference Saved
    {
        get
        {
            var current = Current;
            return new ReplyPreference(current.Language, current.Script, current.AnswerLength);
        }
    }

    public IReadOnlyList<string> StyleNotes => Current.StyleNotes.ToList();

    /// <summary>Merges <paramref name="change"/> into the saved preferences ("from now on…", sidebar, /lang).</summary>
    public void Apply(ReplyPreference change) => Change(p =>
    {
        if (change.Language is { } language)
            p.Language = language == ReplyLanguage.Auto ? null : language;
        if (change.Script is { } script)
            p.Script = script == ReplyScript.Auto ? null : script;
        if (change.Length is { } length)
            p.AnswerLength = length == AnswerLength.Auto ? null : length;
    });

    /// <summary>Back to Auto: no saved language, alphabet, length or style notes.</summary>
    public void Reset() => Change(p =>
    {
        p.Language = null;
        p.Script = null;
        p.AnswerLength = null;
        p.StyleNotes.Clear();
    });

    public void AddStyleNote(string note)
    {
        if (string.IsNullOrWhiteSpace(note))
            throw new ArgumentException("The style note is empty.", nameof(note));

        Change(p =>
        {
            p.StyleNotes.RemoveAll(n => string.Equals(n, note.Trim(), StringComparison.OrdinalIgnoreCase));
            p.StyleNotes.Add(note.Trim());
            if (p.StyleNotes.Count > MaxStyleNotes)
                p.StyleNotes.RemoveAt(0);
        });
    }

    public void ClearStyleNotes() => Change(p => p.StyleNotes.Clear());

    /// <summary>
    /// Turns saving to disk on or off. Turning it off removes the saved preferences from the file;
    /// they stay in use until ShoppyAI is closed.
    /// </summary>
    /// <exception cref="IOException">The file could not be written; nothing was changed.</exception>
    public void SetRemember(bool remember)
    {
        var current = Current;
        if (remember == current.Remember)
            return;

        var updated = Copy(current);
        updated.Remember = remember;

        // When off, only the switch itself is written.
        _store?.Update(s => s.Preferences = remember ? Copy(updated) : new PreferenceSettings { Remember = false });

        _memory = updated;
        _unsaved = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private PreferenceSettings Current
    {
        get
        {
            if (_store is null || _unsaved)
                return _memory;

            // Follow the file, which another ShoppyAI process may have changed; "remember off"
            // there means this process stops saving too.
            var stored = _store.Settings.Preferences;
            if (stored.Remember)
                _memory = Copy(stored);
            else
                _memory.Remember = false;

            return _memory;
        }
    }

    /// <summary>
    /// Applies a change in memory, then saves it. If saving fails the change still applies until
    /// ShoppyAI is closed, and the error is rethrown so the user can be told.
    /// </summary>
    private void Change(Action<PreferenceSettings> change)
    {
        var updated = Copy(Current);
        change(updated);
        _memory = updated;

        try
        {
            if (_store is not null && updated.Remember)
            {
                _store.Update(s => s.Preferences = Copy(updated));
                _unsaved = false;
            }
        }
        catch
        {
            _unsaved = true;
            throw;
        }
        finally
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static PreferenceSettings Copy(PreferenceSettings p) => new()
    {
        Remember = p.Remember,
        Language = p.Language,
        Script = p.Script,
        AnswerLength = p.AnswerLength,
        StyleNotes = p.StyleNotes.ToList()
    };
}
