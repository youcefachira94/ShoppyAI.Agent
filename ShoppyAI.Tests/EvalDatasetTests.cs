using System.Text.Json;
using ShoppyAI.Core.Language;

namespace ShoppyAI.Tests;

/// <summary>Checks the evaluation set used against the real model (ShoppyAI.DarijaEval).</summary>
public class EvalDatasetTests
{
    private static readonly JsonElement[] Cases = LoadCases();

    private static JsonElement[] LoadCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "darija-eval-dataset.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.Clone()).ToArray();
    }

    private static IEnumerable<string> UserMessages(string category) => Cases
        .Where(c => c.GetProperty("category").GetString() == category)
        .SelectMany(c => c.GetProperty("turns").EnumerateArray())
        .Select(t => t.GetProperty("user").GetString()!);

    [Theory]
    [InlineData("darija-arabic", 20)]
    [InlineData("arabizi", 20)]
    [InlineData("mixed", 10)]
    [InlineData("conversation", 10)]
    [InlineData("ambiguous", 10)]
    public void Has_the_required_number_of_cases(string category, int expected)
    {
        Assert.Equal(expected, Cases.Count(c => c.GetProperty("category").GetString() == category));
    }

    [Fact]
    public void Ids_are_unique_and_conversations_have_several_turns()
    {
        Assert.Equal(Cases.Length, Cases.Select(c => c.GetProperty("id").GetString()).Distinct().Count());
        Assert.All(Cases.Where(c => c.GetProperty("category").GetString() == "conversation"),
            c => Assert.True(c.GetProperty("turns").GetArrayLength() >= 2 || c.GetProperty("turns")[0].TryGetProperty("correctionsBefore", out _)));
    }

    [Fact]
    public void Detector_reads_the_arabic_set_as_darija_in_arabic_script()
    {
        var misread = UserMessages("darija-arabic")
            .Select(m => (Message: m, Detection: TestData.Detector.Detect(m)))
            .Where(x => x.Detection.Language != DetectedLanguage.Darija || x.Detection.Script != WritingScript.Arabic)
            .Select(x => $"{x.Message} -> {x.Detection.Language}/{x.Detection.Script}")
            .ToList();

        Assert.Empty(misread);
    }

    [Fact]
    public void Detector_reads_the_arabizi_set_as_darija_in_latin_letters()
    {
        var misread = UserMessages("arabizi")
            .Select(m => (Message: m, Detection: TestData.Detector.Detect(m)))
            .Where(x => x.Detection.Language != DetectedLanguage.Darija || x.Detection.Script != WritingScript.Latin)
            .Select(x => $"{x.Message} -> {x.Detection.Language}/{x.Detection.Script}")
            .ToList();

        Assert.Empty(misread);
    }
}
