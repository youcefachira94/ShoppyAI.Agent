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

public sealed class ShoppySettings
{
    [JsonConverter(typeof(JsonStringEnumConverter<ResponseLanguage>))]
    public ResponseLanguage ResponseLanguage { get; set; } = ResponseLanguage.Auto;

    public OllamaOptions Ollama { get; set; } = new();
}

/// <summary>Loads and saves settings.json.</summary>
public sealed class SettingsStore
{
    private readonly string _filePath;

    public SettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? ShoppyPaths.SettingsFile;
        Settings = Load();
    }

    public ShoppySettings Settings { get; }

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

    /// <summary>Set when settings.json could not be read; defaults are used instead.</summary>
    public string? LoadError { get; private set; }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporary = _filePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Settings, JsonOptions));
        File.Move(temporary, _filePath, overwrite: true);
    }

    private ShoppySettings Load()
    {
        var settings = new ShoppySettings();

        if (File.Exists(_filePath))
        {
            try
            {
                settings = JsonSerializer.Deserialize<ShoppySettings>(File.ReadAllText(_filePath), JsonOptions) ?? settings;
            }
            catch (Exception error) when (error is JsonException or IOException)
            {
                LoadError = error.Message;
            }
        }

        settings.Ollama ??= new OllamaOptions();
        return settings;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
