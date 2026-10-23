using System.Text;

namespace ShoppyAI.Core.Language;

/// <summary>Everything that varies from one turn to the next in the system prompt.</summary>
public sealed record PromptContext(
    LanguageDetection Detection,
    LanguageDetection? PreviousDetection,
    ResponseLanguage Preference,
    IReadOnlyList<GlossaryEntry> GlossaryMatches,
    IReadOnlyList<LanguageCorrection> Corrections,
    int DroppedMessages = 0);

/// <summary>
/// Builds the system message: a fixed part (profile and examples, identical on every turn
/// so Ollama can reuse its cache) followed by a short part for the current turn.
/// </summary>
public sealed class SystemPromptBuilder
{
    public const int MaxCorrections = 40;

    public SystemPromptBuilder(LanguageResources resources)
    {
        FixedPart = BuildFixedPart(resources);
    }

    public string FixedPart { get; }

    public string Build(PromptContext context)
    {
        var prompt = new StringBuilder(FixedPart);
        prompt.AppendLine().AppendLine();
        prompt.AppendLine("# Current turn");
        prompt.AppendLine($"- Language detected in the user's latest message (automatic guess, may be wrong): {context.Detection.Describe()}.");
        if (context.Detection.IsWeak && context.PreviousDetection is { } previous)
            prompt.AppendLine($"- The message is short or unclear; the conversation so far is in: {previous.Describe()}.");
        prompt.AppendLine($"- Reply language: {ReplyInstruction(context.Preference, context.Detection, context.PreviousDetection)}");

        if (context.Detection.OtherVarietyHints.Count > 0)
        {
            var words = context.Detection.OtherVarietyHints
                .DistinctBy(h => h.Word.ToLowerInvariant())
                .Select(h => $"\"{h.Word}\" ({h.Variety})");
            prompt.AppendLine($"- Words that are typical of another variety: {string.Join(", ", words)}. " +
                              "Understand them, do not correct the user, and ask if a meaning is unclear.");
        }

        if (context.DroppedMessages > 0)
        {
            prompt.AppendLine($"- The {context.DroppedMessages} oldest messages of this conversation were removed to fit the context window. " +
                              "If the user refers to them, say that you no longer see them.");
        }

        if (context.GlossaryMatches.Count > 0)
        {
            prompt.AppendLine().AppendLine("# Glossary for words in this conversation");
            foreach (var entry in context.GlossaryMatches)
                prompt.AppendLine(FormatEntry(entry));
        }

        var corrections = context.Corrections.Where(c => c.Enabled).Take(MaxCorrections).ToList();
        if (corrections.Count > 0)
        {
            prompt.AppendLine().AppendLine("# User corrections (confirmed by the user, highest priority)");
            foreach (var correction in corrections)
            {
                var note = correction.Note is null ? "" : $" ({correction.Note})";
                prompt.AppendLine($"- \"{correction.Term}\" = {correction.Meaning}{note}");
            }
        }

        return prompt.ToString().TrimEnd();
    }

    public static string ReplyInstruction(
        ResponseLanguage preference, LanguageDetection detection, LanguageDetection? previous)
    {
        const string chosen = " The user chose this in the settings, whatever language they write in.";

        switch (preference)
        {
            case ResponseLanguage.DarijaLatin:
                return "Algerian Darija written in Latin letters (Arabizi: 3 = ع, 7 = ح, 9 = ق)." + chosen;
            case ResponseLanguage.DarijaArabic:
                return "Algerian Darija written in Arabic script, not Modern Standard Arabic; code and technical terms stay in Latin characters." + chosen;
            case ResponseLanguage.French:
                return "French." + chosen;
            case ResponseLanguage.English:
                return "English." + chosen;
            case ResponseLanguage.StandardArabic:
                return "Modern Standard Arabic." + chosen;
        }

        // A short or unclear message keeps the language of the conversation.
        var reference = detection.IsWeak && previous is not null ? previous : detection;

        return (reference?.Language, reference?.Script) switch
        {
            (DetectedLanguage.Darija, WritingScript.Arabic) =>
                "Algerian Darija in Arabic script, like the user, not Modern Standard Arabic. Keep code and technical terms in Latin characters.",
            (DetectedLanguage.Darija, _) =>
                "Algerian Darija in Latin letters (Arabizi), like the user, with digits for Arabic letters as they do (3 = ع, 7 = ح, 9 = ق). Keep French or English technical words as the user writes them.",
            (DetectedLanguage.StandardArabic, _) => "Modern Standard Arabic, like the user.",
            (DetectedLanguage.ArabicUnspecified, _) =>
                "Arabic script, in the user's register: Algerian Darija if the message is dialectal, otherwise simple Modern Standard Arabic.",
            (DetectedLanguage.French, _) => "French, like the user.",
            (DetectedLanguage.English, _) => "English, like the user.",
            _ => "the language the user writes in; if it is unclear, French."
        };
    }

    private static string FormatEntry(GlossaryEntry entry)
    {
        var otherForms = entry.Forms
            .Where(f => !string.Equals(f, entry.Term, StringComparison.OrdinalIgnoreCase))
            .Take(4)
            .ToList();
        var forms = otherForms.Count > 0 ? $" ({string.Join(", ", otherForms)})" : "";
        var dialect = entry.Dialect == "DZ" ? "" : $" [{entry.Dialect}]";
        var note = entry.Note is null ? "" : $" — {entry.Note}";
        return $"- {entry.Term}{forms}{dialect}: {entry.Meaning}{note}";
    }

    private static string BuildFixedPart(LanguageResources resources)
    {
        var prompt = new StringBuilder(resources.BaseSystemPrompt);

        if (resources.Examples.Count > 0)
        {
            prompt.AppendLine().AppendLine();
            prompt.AppendLine("# Examples of the expected style");
            prompt.AppendLine("These are examples only, not part of the conversation.");

            foreach (var example in resources.Examples)
            {
                prompt.AppendLine().AppendLine($"## {example.Title}");
                foreach (var turn in example.Turns)
                {
                    var speaker = turn.Role == "user" ? "User" : "ShoppyAI";
                    prompt.AppendLine($"{speaker}: {turn.Content}");
                }
            }
        }

        return prompt.ToString().TrimEnd();
    }
}
