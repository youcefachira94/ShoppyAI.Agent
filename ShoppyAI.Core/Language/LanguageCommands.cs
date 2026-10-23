using System.Text;

namespace ShoppyAI.Core.Language;

public static class ResponseLanguageNames
{
    public static string DisplayName(ResponseLanguage language) => language switch
    {
        ResponseLanguage.DarijaLatin => "Darija (Latin / Arabizi)",
        ResponseLanguage.DarijaArabic => "Darija (العربية)",
        ResponseLanguage.French => "Français",
        ResponseLanguage.English => "English",
        ResponseLanguage.StandardArabic => "العربية الفصحى",
        _ => "Auto (same as you)"
    };

    public static bool TryParse(string value, out ResponseLanguage language)
    {
        language = value.Trim().ToLowerInvariant() switch
        {
            "auto" => ResponseLanguage.Auto,
            "darija" or "darija-latin" or "arabizi" or "latin" or "dz" => ResponseLanguage.DarijaLatin,
            "darija-ar" or "darija-arabic" or "darija-arabe" or "دارجة" => ResponseLanguage.DarijaArabic,
            "fr" or "french" or "francais" or "français" => ResponseLanguage.French,
            "en" or "english" or "anglais" => ResponseLanguage.English,
            "ar" or "msa" or "fusha" or "arabe" or "فصحى" => ResponseLanguage.StandardArabic,
            _ => (ResponseLanguage)(-1)
        };

        return Enum.IsDefined(language);
    }
}

/// <summary>
/// Chat commands to manage the reply language and the language corrections, shared by
/// the desktop chat and the console agent. They never reach the model.
/// </summary>
public sealed class LanguageCommands
{
    private readonly CorrectionStore _corrections;
    private readonly Func<ResponseLanguage> _getLanguage;
    private readonly Action<ResponseLanguage> _setLanguage;

    public LanguageCommands(CorrectionStore corrections, Func<ResponseLanguage> getLanguage, Action<ResponseLanguage> setLanguage)
    {
        _corrections = corrections;
        _getLanguage = getLanguage;
        _setLanguage = setLanguage;
    }

    public const string Help =
        "/lang [auto|darija|darija-ar|fr|en|ar]   show or set the reply language\n" +
        "/correct <word> = <meaning> [| note]     teach or fix the meaning of a word\n" +
        "/corrections                             list your corrections\n" +
        "/disable <word>, /enable <word>          turn a correction off or back on\n" +
        "/forget <word>                           delete a correction\n" +
        "/undo                                    undo the last correction change";

    /// <summary>Runs <paramref name="input"/> if it is a language command.</summary>
    /// <returns>The text to show to the user, or null when the input is not a language command.</returns>
    public string? TryExecute(string input)
    {
        var text = input.Trim();
        if (!text.StartsWith('/'))
            return null;

        var space = text.IndexOf(' ');
        var command = (space < 0 ? text : text[..space]).ToLowerInvariant();
        var argument = space < 0 ? "" : text[(space + 1)..].Trim();

        return command switch
        {
            "/lang" or "/langue" => Language(argument),
            "/correct" or "/corriger" => Correct(argument),
            "/corrections" => List(),
            "/disable" or "/desactiver" => Toggle(argument, false),
            "/enable" or "/activer" => Toggle(argument, true),
            "/forget" or "/supprimer" => Forget(argument),
            "/undo" or "/annuler" => Undo(),
            "/help-lang" => Help,
            _ => null
        };
    }

    private string Language(string argument)
    {
        if (argument.Length == 0)
            return $"Reply language: {ResponseLanguageNames.DisplayName(_getLanguage())}.";

        if (!ResponseLanguageNames.TryParse(argument, out var language))
            return "Unknown language. Use: auto, darija, darija-ar, fr, en or ar.";

        _setLanguage(language);
        return $"Reply language set to {ResponseLanguageNames.DisplayName(language)}.";
    }

    private string Correct(string argument)
    {
        var equals = argument.IndexOf('=');
        if (equals <= 0)
            return "Usage: /correct <word> = <meaning> [| note]   e.g. /correct doka = maintenant";

        var term = argument[..equals].Trim().Trim('"', '\'', '«', '»');
        var rest = argument[(equals + 1)..];
        var pipe = rest.IndexOf('|');
        var meaning = (pipe < 0 ? rest : rest[..pipe]).Trim();
        var note = pipe < 0 ? null : rest[(pipe + 1)..].Trim();

        if (term.Length == 0 || meaning.Length == 0)
            return "Usage: /correct <word> = <meaning> [| note]";

        var existed = _corrections.Find(term) is not null;
        var correction = _corrections.AddOrUpdate(term, meaning, note);
        return $"{(existed ? "Updated" : "Saved")}: \"{correction.Term}\" = {correction.Meaning}. (/undo to revert)";
    }

    private string List()
    {
        var all = _corrections.All;
        if (all.Count == 0)
            return "No corrections yet. Example: /correct doka = maintenant";

        var builder = new StringBuilder("Your corrections:");
        foreach (var c in all)
        {
            var state = c.Enabled ? "" : " (disabled)";
            var note = c.Note is null ? "" : $" — {c.Note}";
            builder.Append($"\n- \"{c.Term}\" = {c.Meaning}{note}{state}");
        }

        return builder.ToString();
    }

    private string Toggle(string term, bool enabled)
    {
        if (term.Length == 0)
            return $"Usage: /{(enabled ? "enable" : "disable")} <word>";

        if (_corrections.Find(term) is null)
            return $"No correction for \"{term}\".";

        _corrections.SetEnabled(term, enabled);
        return $"\"{term}\" {(enabled ? "enabled" : "disabled")}. (/undo to revert)";
    }

    private string Forget(string term)
    {
        if (term.Length == 0)
            return "Usage: /forget <word>";

        return _corrections.Remove(term)
            ? $"\"{term}\" deleted. (/undo to restore it)"
            : $"No correction for \"{term}\".";
    }

    private string Undo() =>
        _corrections.Undo() is { } undone
            ? $"Undone: {undone}."
            : "Nothing to undo.";
}
