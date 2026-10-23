using System.Text;
using ShoppyAI.Core.Ollama;

namespace ShoppyAI.DarijaEval;

/// <summary>Markdown report: check counts per category, then every answer for human review.</summary>
public static class Report
{
    private static readonly string[] Categories = ["darija-arabic", "arabizi", "mixed", "conversation", "ambiguous"];

    public static string Build(IReadOnlyList<TurnResult> results, OllamaOptions options, bool dryRun, TimeSpan duration)
    {
        var report = new StringBuilder();
        report.AppendLine("# ShoppyAI — Darija DZ evaluation");
        report.AppendLine();
        report.AppendLine($"- Date: {DateTime.Now:yyyy-MM-dd HH:mm}");
        report.AppendLine(dryRun
            ? "- Dry run: no model was called."
            : $"- Model: `{options.Model}` at `{options.BaseUrl}` (num_ctx {options.ContextLength}, temperature {options.Temperature}, seed {options.Seed}{(options.MaxAnswerTokens is { } n ? $", num_predict {n}" : "")})");
        report.AppendLine($"- Turns: {results.Count}, duration: {duration:hh\\:mm\\:ss}");
        report.AppendLine();
        report.AppendLine("The automatic checks are heuristics (script, language markers, question mark, keywords). " +
                          "They point to answers to read first; they are not a comprehension score. Read every answer below.");
        report.AppendLine();

        if (!dryRun)
        {
            report.AppendLine("## Checks per category");
            report.AppendLine();
            report.AppendLine("| Category | Turns | Turns with a failed check | Failed checks by type |");
            report.AppendLine("|---|---|---|---|");
            foreach (var category in Categories.Concat(results.Select(r => r.Case.Category)).Distinct())
            {
                var turns = results.Where(r => r.Case.Category == category).ToList();
                if (turns.Count == 0)
                    continue;

                var failed = turns.SelectMany(t => t.Checks).Where(c => !c.Passed)
                    .GroupBy(c => c.Name)
                    .Select(g => $"{g.Key}: {g.Count()}");
                report.AppendLine($"| {category} | {turns.Count} | {turns.Count(t => t.Checks.Any(c => !c.Passed))} | {string.Join(", ", failed)} |");
            }

            report.AppendLine();
        }

        report.AppendLine("## Answers");
        foreach (var group in results.GroupBy(r => r.Case.Id))
        {
            var first = group.First();
            report.AppendLine();
            report.AppendLine($"### {first.Case.Id} ({first.Case.Category})");
            if (first.Case.Note is not null)
                report.AppendLine($"_{first.Case.Note}_");
            if (first.Case.Preference is not null)
                report.AppendLine($"Reply language preference: `{first.Case.Preference}`");

            foreach (var turn in group)
            {
                report.AppendLine();
                foreach (var correction in turn.Turn.CorrectionsBefore ?? [])
                    report.AppendLine($"- Correction added before this turn: \"{correction.Term}\" = {correction.Meaning}");

                report.AppendLine($"**User:** {turn.Turn.User}");
                report.AppendLine();
                report.AppendLine($"Detected: {turn.InputDetection.Describe()} · glossary: {(turn.Glossary.Count == 0 ? "none" : string.Join(", ", turn.Glossary))} · system prompt {turn.SystemPromptChars} chars");

                if (dryRun)
                    continue;

                report.AppendLine();
                report.AppendLine("**ShoppyAI:**");
                report.AppendLine();
                report.AppendLine("````text");
                report.AppendLine(turn.Reply.Replace("````", "``​``"));
                report.AppendLine("````");
                report.AppendLine();
                var timing = turn.Metrics is null
                    ? $"{turn.Elapsed.TotalSeconds:0.0}s"
                    : $"{turn.Elapsed.TotalSeconds:0.0}s, {turn.Metrics.PromptTokens} prompt tokens, {turn.Metrics.CompletionTokens} answer tokens";
                report.AppendLine($"Answer reads as: {turn.ReplyDetection?.Describe() ?? "n/a"} · {timing}");
                foreach (var check in turn.Checks)
                    report.AppendLine($"- {(check.Passed ? "pass" : "**FAIL**")} {check.Name}: {check.Detail}");
            }
        }

        return report.ToString();
    }
}
