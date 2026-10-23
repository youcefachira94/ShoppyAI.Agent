using System.Text;
using ShoppyAI.Core.Chat;

namespace ShoppyAI.Core.Language;

/// <summary>Names used by /lang and in settings screens for reply preferences.</summary>
public static class ReplyPreferenceNames
{
    /// <summary>
    /// Parses a /lang value into what it sets. Language and Darija alphabet are separate
    /// preferences: only darija-ar / darija-latin change the alphabet, and auto resets both.
    /// </summary>
    public static bool TryParse(string value, out ReplyPreference preference)
    {
        preference = value.Trim().ToLowerInvariant() switch
        {
            "auto" => new ReplyPreference(ReplyLanguage.Auto, ReplyScript.Auto),
            "darija" or "dz" => new ReplyPreference(ReplyLanguage.Darija),
            "darija-ar" or "darija-arabic" or "darija-arabe" or "دارجة" => new ReplyPreference(ReplyLanguage.Darija, ReplyScript.Arabic),
            "darija-latin" or "arabizi" or "latin" => new ReplyPreference(ReplyLanguage.Darija, ReplyScript.Latin),
            "fr" or "french" or "francais" or "français" => new ReplyPreference(ReplyLanguage.French),
            "en" or "english" or "anglais" => new ReplyPreference(ReplyLanguage.English),
            "ar" or "msa" or "fusha" or "فصحى" => new ReplyPreference(ReplyLanguage.StandardArabic, ReplyScript.Arabic),
            _ => ReplyPreference.None
        };

        return !preference.IsEmpty;
    }

    public static string Language(ReplyLanguage? language) => language switch
    {
        ReplyLanguage.Darija => "Darija",
        ReplyLanguage.French => "Français",
        ReplyLanguage.English => "English",
        ReplyLanguage.StandardArabic => "العربية الفصحى",
        _ => "Auto"
    };

    public static string Script(ReplyScript? script) => script switch
    {
        ReplyScript.Arabic => "Arabic letters",
        ReplyScript.Latin => "Latin letters (Arabizi)",
        _ => "Auto"
    };

    public static string Length(AnswerLength? length) => length switch
    {
        AnswerLength.Short => "short",
        AnswerLength.Detailed => "detailed",
        _ => "Auto"
    };

    /// <summary>Short description of a preference layer, e.g. "Darija, Arabic letters".</summary>
    public static string Describe(ReplyPreference preference)
    {
        var parts = new List<string>();
        if (preference.Language is { } language)
            parts.Add(language == ReplyLanguage.Auto ? "same language as you" : Language(language));
        if (preference.Script is { } script && script != ReplyScript.Auto)
            parts.Add(Script(script));
        if (preference.Length is { } length && length != AnswerLength.Auto)
            parts.Add($"{Length(length)} answers");
        return parts.Count == 0 ? "Auto" : string.Join(", ", parts);
    }
}

/// <summary>
/// Chat commands to manage reply preferences and language corrections, shared by the
/// desktop chat and the console agent. They never reach the model.
/// </summary>
public sealed class LanguageCommands
{
    private readonly CorrectionStore _corrections;
    private readonly LanguagePreferences _preferences;
    private readonly ChatSession? _session;

    public LanguageCommands(CorrectionStore corrections, LanguagePreferences preferences, ChatSession? session = null)
    {
        _corrections = corrections;
        _preferences = preferences;
        _session = session;
    }

    public const string Help =
        "Replies follow your language automatically; you can also just ask (\"réponds en français\", \"من اليوم جاوبني بالعربية\").\n" +
        "/prefs                                   show your reply preferences\n" +
        "/prefs reset                             back to automatic (saved and this chat)\n" +
        "/lang [auto|darija|darija-ar|darija-latin|fr|en|ar]   show or save the reply language\n" +
        "/alphabet [auto|arabic|latin]            save the alphabet used for Darija\n" +
        "/length [auto|short|detailed]            save the answer length\n" +
        "/style <note> | /style | /style clear    add, list or clear style notes\n" +
        "/remember [on|off]                       keep preferences for new chats\n" +
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

        try
        {
            return command switch
            {
                "/prefs" or "/preferences" => Preferences(argument),
                "/lang" or "/langue" => Language(argument),
                "/alphabet" or "/script" => Alphabet(argument),
                "/length" or "/longueur" => Length(argument),
                "/style" => Style(argument),
                "/remember" or "/memoriser" => Remember(argument),
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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"The change could not be saved, nothing was changed ({error.Message}).";
        }
    }

    private string Preferences(string argument)
    {
        if (argument.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            _preferences.Reset();
            _session?.ResetConversationRequests();
            return "Preferences reset: replies follow your language again.";
        }

        var saved = _preferences.Saved;
        var builder = new StringBuilder("Reply preferences:");
        builder.Append($"\n- language: {ReplyPreferenceNames.Language(saved.Language)}");
        builder.Append($"\n- alphabet for Darija: {ReplyPreferenceNames.Script(saved.Script)}");
        builder.Append($"\n- answer length: {ReplyPreferenceNames.Length(saved.Length)}");
        builder.Append($"\n- kept for new chats: {(_preferences.Remember ? "yes" : "no (until ShoppyAI is closed)")}");
        foreach (var note in _preferences.StyleNotes)
            builder.Append($"\n- style: {note}");
        if (_session is { ConversationRequests.IsEmpty: false } session)
            builder.Append($"\nThis chat: {ReplyPreferenceNames.Describe(session.ConversationRequests)} (asked in the conversation).");
        return builder.ToString();
    }

    private string Language(string argument)
    {
        if (argument.Length == 0)
            return Preferences("");

        if (!ReplyPreferenceNames.TryParse(argument, out var preference))
            return "Unknown language. Use: auto, darija, darija-ar, darija-latin, fr, en or ar.";

        _preferences.Apply(preference);
        _session?.ResetConversationRequests(preference);
        return $"Reply language saved: {ReplyPreferenceNames.Describe(preference)}.";
    }

    private string Alphabet(string argument)
    {
        ReplyScript? script = argument.ToLowerInvariant() switch
        {
            "auto" => ReplyScript.Auto,
            "arabic" or "arabe" or "ar" or "عربية" => ReplyScript.Arabic,
            "latin" or "arabizi" => ReplyScript.Latin,
            _ => null
        };

        if (script is null)
            return $"Alphabet for Darija: {ReplyPreferenceNames.Script(_preferences.Saved.Script)}. Use: /alphabet auto|arabic|latin";

        _preferences.Apply(new ReplyPreference(Script: script));
        _session?.ResetConversationRequests(new ReplyPreference(Script: script));
        return $"Alphabet for Darija saved: {ReplyPreferenceNames.Script(script)}.";
    }

    private string Length(string argument)
    {
        AnswerLength? length = argument.ToLowerInvariant() switch
        {
            "auto" => AnswerLength.Auto,
            "short" or "court" => AnswerLength.Short,
            "detailed" or "long" or "detaille" or "détaillé" => AnswerLength.Detailed,
            _ => null
        };

        if (length is null)
            return $"Answer length: {ReplyPreferenceNames.Length(_preferences.Saved.Length)}. Use: /length auto|short|detailed";

        _preferences.Apply(new ReplyPreference(Length: length));
        _session?.ResetConversationRequests(new ReplyPreference(Length: length));
        return $"Answer length saved: {ReplyPreferenceNames.Length(length)}.";
    }

    private string Style(string argument)
    {
        if (argument.Length == 0)
        {
            var notes = _preferences.StyleNotes;
            return notes.Count == 0
                ? "No style notes. Example: /style tutoie-moi et évite les emojis"
                : "Style notes:" + string.Concat(notes.Select(n => $"\n- {n}"));
        }

        if (argument.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            _preferences.ClearStyleNotes();
            return "Style notes cleared.";
        }

        _preferences.AddStyleNote(argument);
        return $"Style note saved: {argument}";
    }

    private string Remember(string argument)
    {
        bool? remember = argument.ToLowerInvariant() switch
        {
            "on" or "yes" or "oui" => true,
            "off" or "no" or "non" => false,
            _ => null
        };

        if (remember is null)
            return $"Preferences are {(_preferences.Remember ? "" : "not ")}kept for new chats. Use: /remember on|off";

        _preferences.SetRemember(remember.Value);
        return remember.Value
            ? "Your preferences will be kept for new chats."
            : "Your preferences now last until ShoppyAI is closed; they are no longer saved.";
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

        var state = enabled ? "enabled" : "disabled";
        if (_corrections.Find(term) is null)
            return $"No correction for \"{term}\".";

        return _corrections.SetEnabled(term, enabled)
            ? $"\"{term}\" {state}. (/undo to revert)"
            : $"\"{term}\" is already {state}.";
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
