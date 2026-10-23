using ShoppyAI.Core.Language;
using ShoppyAI.Core.Text;

namespace ShoppyAI.DarijaEval;

public sealed record CheckResult(string Name, bool Passed, string Detail);

/// <summary>
/// Automatic heuristic checks on one answer. They flag answers to read first; they do
/// not measure understanding, and a passing answer can still be wrong.
/// </summary>
public static class EvalChecks
{
    /// <summary>
    /// Checks the reply language ShoppyAI asked the model for. This part is deterministic
    /// (no model involved), so it is also checked in --dry-run.
    /// </summary>
    public static List<CheckResult> Decision(EvalExpectation expect, ReplyDecision decision)
    {
        var checks = new List<CheckResult>();
        if (expect.Length is { } expectedLength)
        {
            var actualLength = decision.Length.ToString().ToLowerInvariant();
            checks.Add(new CheckResult("answer-length", actualLength == expectedLength, $"expected {expectedLength}, ShoppyAI asked for {actualLength}"));
        }

        if (expect.Reply is null)
            return checks;

        var actual = (decision.Language, decision.Script) switch
        {
            (ReplyLanguage.Darija, ReplyScript.Arabic) => "darija-ar",
            (ReplyLanguage.Darija, _) => "darija-latin",
            (ReplyLanguage.French, _) => "fr",
            (ReplyLanguage.English, _) => "en",
            (ReplyLanguage.StandardArabic, _) => "ar",
            _ => "auto"
        };

        checks.Insert(0, new CheckResult("reply-language", actual == expect.Reply, $"expected {expect.Reply}, ShoppyAI asked for {actual} ({decision.Source})"));
        return checks;
    }

    public static List<CheckResult> Run(EvalExpectation expect, string reply, LanguageDetection replyDetection)
    {
        var checks = new List<CheckResult>();
        var prose = TextNormalizer.StripCodeAndUrls(reply);

        if (expect.Script != "any")
        {
            var expected = expect.Script == "arabic" ? WritingScript.Arabic : WritingScript.Latin;
            checks.Add(new CheckResult("script", replyDetection.Script == expected,
                $"expected {expected}, answer is {replyDetection.Script}"));
        }

        if (expect.Language != "any")
        {
            var expected = expect.Language switch
            {
                "darija" => DetectedLanguage.Darija,
                "french" => DetectedLanguage.French,
                "english" => DetectedLanguage.English,
                "msa" => DetectedLanguage.StandardArabic,
                _ => DetectedLanguage.Unknown
            };
            checks.Add(new CheckResult("language", replyDetection.Language == expected,
                $"expected {expected}, answer reads as {replyDetection.Language} " +
                $"(darija {replyDetection.DarijaScore:0.#}, fr {replyDetection.FrenchScore:0.#}, " +
                $"en {replyDetection.EnglishScore:0.#}, msa {replyDetection.StandardArabicScore:0.#})"));
        }

        if (expect.Language == "darija" && !expect.AllowOtherVarieties)
        {
            var hints = replyDetection.OtherVarietyHints;
            checks.Add(new CheckResult("algerian-forms", hints.Count == 0,
                hints.Count == 0 ? "no Moroccan/Tunisian/Middle-Eastern forms" : string.Join(", ", hints.Select(h => $"{h.Word} ({h.Variety})"))));
        }

        if (expect.AskClarification)
        {
            var asks = prose.Contains('?') || prose.Contains('؟');
            checks.Add(new CheckResult("asks-question", asks, asks ? "contains a question" : "no question mark in the answer"));
        }

        if (expect.MentionsAny is { Count: > 0 } any)
        {
            var found = any.Where(k => reply.Contains(k, StringComparison.OrdinalIgnoreCase)).ToList();
            checks.Add(new CheckResult("mentions-any", found.Count > 0,
                found.Count > 0 ? $"found: {string.Join(", ", found)}" : $"none of: {string.Join(", ", any)}"));
        }

        if (expect.MentionsAll is { Count: > 0 } all)
        {
            var missing = all.Where(k => !reply.Contains(k, StringComparison.OrdinalIgnoreCase)).ToList();
            checks.Add(new CheckResult("mentions-all", missing.Count == 0,
                missing.Count == 0 ? "all present" : $"missing: {string.Join(", ", missing)}"));
        }

        return checks;
    }
}
