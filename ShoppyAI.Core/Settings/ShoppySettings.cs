using System.Text.Json;
using System.Text.Json.Serialization;
using ShoppyAI.Core.Language;
using ShoppyAI.Core.Ollama;

namespace ShoppyAI.Core.Settings;

/// <summary>Where ShoppyAI keeps its local files (settings and language corrections, never conversations).</summary>
public static class ShoppyPaths
{
    public static string DataDirectory =>
        Environment.GetEnvironmentVariable("SHOPPYAI_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShoppyAI");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string LanguageDirectory => Path.Combine(DataDirectory, "language");
    public static string CorrectionsFile => Path.Combine(LanguageDirectory, "corrections.json");
}

/// <summary>Saved reply preferences. Null means "follow the conversation" (Auto).</summary>
public sealed class PreferenceSettings
{
    /// <summary>Keep the preferences for new conversations and after a restart.</summary>
    public bool Remember { get; set; } = true;

    public ReplyLanguage? Language { get; set; }
    public ReplyScript? Script { get; set; }
    public AnswerLength? AnswerLength { get; set; }
    public List<string> StyleNotes { get; set; } = [];
}

public sealed class ShoppySettings
{
    /// <summary>Version 1 setting (Auto, DarijaLatin, DarijaArabic…), migrated to <see cref="Preferences"/>.</summary>
    [JsonPropertyName("responseLanguage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyResponseLanguage { get; set; }

    public PreferenceSettings Preferences { get; set; } = new();

    public OllamaOptions Ollama { get; set; } = new();
}

/// <summary>
/// Loads and saves settings.json. The file is re-read when another ShoppyAI process
/// (console or desktop) changed it, so one does not overwrite the other's preferences.
/// A file that cannot be read (being written, locked, or edited by hand with a mistake)
/// is never overwritten: the current settings stay in use and reading is retried.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _filePath;
    private readonly Lock _gate = new();
    private ShoppySettings _settings = new();
    private DateTime _loadedWriteTime;
    private string? _readError;

    public SettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? ShoppyPaths.SettingsFile;
        Refresh(force: true);
        LoadError = _readError;
    }

    /// <summary>Current settings, re-read if the file changed since the last access.</summary>
    public ShoppySettings Settings
    {
        get
        {
            lock (_gate)
            {
                Refresh();
                return _settings;
            }
        }
    }

    /// <summary>Set when settings.json could not be read at start; defaults are used instead.</summary>
    public string? LoadError { get; }

    /// <summary>Ollama options to use: the saved ones, with SHOPPYAI_OLLAMA_URL / SHOPPYAI_MODEL applied.</summary>
    public OllamaOptions EffectiveOllamaOptions()
    {
        var saved = Settings.Ollama;
        return new OllamaOptions
        {
            BaseUrl = Environment.GetEnvironmentVariable("SHOPPYAI_OLLAMA_URL") is { Length: > 0 } url ? url : saved.BaseUrl,
            Model = Environment.GetEnvironmentVariable("SHOPPYAI_MODEL") is { Length: > 0 } model ? model : saved.Model,
            Temperature = saved.Temperature,
            ContextLength = saved.ContextLength,
            ReservedAnswerTokens = saved.ReservedAnswerTokens,
            MaxAnswerTokens = saved.MaxAnswerTokens,
            Seed = saved.Seed
        };
    }

    /// <summary>Applies a change to the latest settings and saves them. On a write error nothing is changed.</summary>
    /// <exception cref="IOException">The file could not be read or written; it was left as it is.</exception>
    public void Update(Action<ShoppySettings> change)
    {
        lock (_gate)
        {
            Refresh();
            if (_readError is not null)
                throw new IOException($"settings.json could not be read, so it was not overwritten ({_readError}). Fix or delete the file.");

            var before = JsonSerializer.Serialize(_settings, JsonOptions);
            change(_settings);

            try
            {
                Write();
            }
            catch
            {
                _settings = JsonSerializer.Deserialize<ShoppySettings>(before, JsonOptions)!;
                throw;
            }
        }
    }

    private void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporary = _filePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_settings, JsonOptions));
        File.Move(temporary, _filePath, overwrite: true);
        _loadedWriteTime = File.GetLastWriteTimeUtc(_filePath);
    }

    /// <summary>Reads the file if it changed. On failure the current settings are kept and reading is retried later.</summary>
    private void Refresh(bool force = false)
    {
        if (!File.Exists(_filePath))
        {
            _readError = null;
            return;
        }

        var writeTime = File.GetLastWriteTimeUtc(_filePath);
        if (!force && writeTime == _loadedWriteTime && _readError is null)
            return;

        try
        {
            var settings = JsonSerializer.Deserialize<ShoppySettings>(File.ReadAllText(_filePath), JsonOptions) ?? new ShoppySettings();
            settings.Ollama ??= new OllamaOptions();
            settings.Preferences ??= new PreferenceSettings();
            settings.Preferences.StyleNotes ??= [];
            MigrateVersion1(settings);

            _settings = settings;
            _loadedWriteTime = writeTime;
            _readError = null;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            _readError = error.Message;
        }
    }

    private static void MigrateVersion1(ShoppySettings settings)
    {
        if (settings.LegacyResponseLanguage is not { } legacy)
            return;

        var preferences = settings.Preferences;
        if (preferences.Language is null && preferences.Script is null)
        {
            (preferences.Language, preferences.Script) = legacy switch
            {
                "DarijaLatin" => (ReplyLanguage.Darija, ReplyScript.Latin),
                "DarijaArabic" => (ReplyLanguage.Darija, ReplyScript.Arabic),
                "French" => (ReplyLanguage.French, (ReplyScript?)null),
                "English" => (ReplyLanguage.English, null),
                "StandardArabic" => (ReplyLanguage.StandardArabic, null),
                _ => ((ReplyLanguage?)null, (ReplyScript?)null)
            };
        }

        settings.LegacyResponseLanguage = null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
