using System.Text;

namespace ShoppyAI.Core.Language;

/// <summary>Everything that varies from one turn to the next in the system prompt.</summary>
public sealed record PromptContext(
    LanguageDetection Detection,
    ReplyDecision Reply,
    IReadOnlyList<GlossaryEntry> GlossaryMatches,
    IReadOnlyList<LanguageCorrection> Corrections,
    IReadOnlyList<string>? StyleNotes = null,
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
        prompt.AppendLine($"- Reply language: {ReplyInstruction(context.Reply)}");
        prompt.AppendLine("- If the user's latest message clearly asks for another reply language or alphabet, follow their request instead.");

        if (context.Reply.Avoided is { } avoided)
            prompt.AppendLine($"- The user asked not to be answered in {Name(avoided)}.");

        switch (context.Reply.Length)
        {
            case AnswerLength.Short:
                prompt.AppendLine("- Answer length: short (a few sentences; code only when needed). The user asked for this.");
                break;
            case AnswerLength.Detailed:
                prompt.AppendLine("- Answer length: detailed, with explanations and examples. The user asked for this.");
                break;
        }

        if (context.StyleNotes is { Count: > 0 } notes)
        {
            prompt.AppendLine("- Style preferences of the user:");
            foreach (var note in notes)
                prompt.AppendLine($"  - {note}");
        }

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

    /// <summary>The "Reply language" line: what to answer in, and why.</summary>
    public static string ReplyInstruction(ReplyDecision reply)
    {
        var language = (reply.Language, reply.Script) switch
        {
            (ReplyLanguage.Darija, ReplyScript.Arabic) =>
                "Algerian Darija written in Arabic script, not Modern Standard Arabic. Keep code, commands, file names, identifiers and technical names in their original Latin spelling.",
            (ReplyLanguage.Darija, _) =>
                "Algerian Darija written in Latin letters (Arabizi, with digits for Arabic letters: 3 = ع, 7 = ح, 9 = ق). Keep French or English technical words as they are.",
            (ReplyLanguage.StandardArabic, _) =>
                "Modern Standard Arabic. Keep code, commands, identifiers and technical names in their original Latin spelling.",
            (ReplyLanguage.English, _) => "English.",
            _ => "French."
        };

        var instruction = reply.Instruction;
        var reason = reply.Source switch
        {
            ReplySource.ThisMessage when instruction?.Scope == PreferenceScope.Turn =>
                "The user asked for this in their latest message, for this answer only.",
            ReplySource.ThisMessage when instruction?.Scope == PreferenceScope.Persistent =>
                "The user just asked for this as a lasting preference: confirm it in a few words in this language, then answer. It is saved for future conversations.",
            ReplySource.ThisMessage =>
                "The user just asked for this: confirm it in a few words in this language, then answer, and keep it for the rest of the conversation.",
            ReplySource.Conversation => "The user asked for this earlier in the conversation.",
            ReplySource.Saved => "This is the user's saved preference: use it even when they write in another language or alphabet.",
            ReplySource.ConversationLanguage =>
                "This is the language of the conversation. The latest message is in another language or too short, which does not change the reply language.",
            _ => "Same language as the user."
        };

        return $"{language} {reason}";
    }

    private static string Name(ReplyLanguage language) => language switch
    {
        ReplyLanguage.Darija => "Darija",
        ReplyLanguage.StandardArabic => "Modern Standard Arabic",
        ReplyLanguage.English => "English",
        ReplyLanguage.French => "French",
        _ => language.ToString()
    };

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
