using System.Text.Json;
using System.Text.Json.Serialization;
using ShoppyAI.Core.Language;

namespace ShoppyAI.DarijaEval;

public sealed class EvalDataset
{
    public List<EvalCase> Cases { get; set; } = [];

    public static EvalDataset Load(string path) =>
        JsonSerializer.Deserialize<EvalDataset>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"{path} is empty.");

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

public sealed class EvalCase
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string? Note { get; set; }

    /// <summary>Reply language preference for the whole case (same values as the /lang command).</summary>
    public string? Preference { get; set; }

    public List<EvalTurn> Turns { get; set; } = [];

    public ResponseLanguage ResponseLanguage =>
        Preference is not null && ResponseLanguageNames.TryParse(Preference, out var language) ? language : ResponseLanguage.Auto;
}

public sealed class EvalTurn
{
    public string User { get; set; } = "";
    public List<EvalCorrection>? CorrectionsBefore { get; set; }
    public EvalExpectation Expect { get; set; } = new();
}

public sealed class EvalCorrection
{
    public string Term { get; set; } = "";
    public string Meaning { get; set; } = "";
    public string? Note { get; set; }
}

public sealed class EvalExpectation
{
    /// <summary>"arabic", "latin" or "any".</summary>
    public string Script { get; set; } = "any";

    /// <summary>"darija", "french", "english", "msa" or "any".</summary>
    public string Language { get; set; } = "any";

    public bool AskClarification { get; set; }
    public List<string>? MentionsAny { get; set; }
    public List<string>? MentionsAll { get; set; }
    public bool AllowOtherVarieties { get; set; }
}
