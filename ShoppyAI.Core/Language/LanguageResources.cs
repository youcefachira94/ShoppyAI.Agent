using System.Text.Json;

namespace ShoppyAI.Core.Language;

public sealed record DialogueTurn(string Role, string Content);

public sealed record DialogueExample(string Title, IReadOnlyList<DialogueTurn> Turns);

/// <summary>
/// The language profile data: base system prompt, glossary, dialogue examples and
/// detector markers. Defaults are embedded files from Language/Data; a user glossary
/// placed in the data directory extends or overrides the default one.
/// </summary>
public sealed class LanguageResources
{
    public const string UserGlossaryFileName = "glossary.user.json";

    public required string BaseSystemPrompt { get; init; }
    public required DarijaGlossary Glossary { get; init; }
    public required IReadOnlyList<DialogueExample> Examples { get; init; }
    public required DialectMarkers Markers { get; init; }

    /// <summary>Error raised while reading the user glossary, if any. The default glossary is used instead.</summary>
    public string? UserGlossaryError { get; init; }

    public static LanguageResources Load(string? languageDirectory = null)
    {
        var glossary = DarijaGlossary.Parse(ReadEmbedded("darija-dz.glossary.json"));
        string? userGlossaryError = null;

        if (languageDirectory is not null)
        {
            var userGlossaryPath = Path.Combine(languageDirectory, UserGlossaryFileName);
            if (File.Exists(userGlossaryPath))
            {
                try
                {
                    glossary = glossary.MergeWith(DarijaGlossary.Parse(File.ReadAllText(userGlossaryPath)));
                }
                catch (Exception error) when (error is JsonException or IOException or InvalidDataException)
                {
                    userGlossaryError = $"{UserGlossaryFileName}: {error.Message}";
                }
            }
        }

        return new LanguageResources
        {
            BaseSystemPrompt = ReadEmbedded("system-prompt.md").Trim(),
            Glossary = glossary,
            Examples = ParseExamples(ReadEmbedded("dialogue-examples.json")),
            Markers = DialectMarkers.Parse(ReadEmbedded("dialect-markers.json")),
            UserGlossaryError = userGlossaryError
        };
    }

    private static IReadOnlyList<DialogueExample> ParseExamples(string json)
    {
        using var document = JsonDocument.Parse(json);
        var examples = new List<DialogueExample>();

        foreach (var example in document.RootElement.GetProperty("examples").EnumerateArray())
        {
            var turns = example.GetProperty("turns").EnumerateArray()
                .Select(t => new DialogueTurn(t.GetProperty("role").GetString()!, t.GetProperty("content").GetString()!))
                .ToList();

            examples.Add(new DialogueExample(example.GetProperty("title").GetString() ?? "", turns));
        }

        return examples;
    }

    private static string ReadEmbedded(string fileName)
    {
        var assembly = typeof(LanguageResources).Assembly;
        using var stream = assembly.GetManifestResourceStream($"ShoppyAI.Language.{fileName}")
                           ?? throw new InvalidOperationException($"Missing embedded language file: {fileName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
