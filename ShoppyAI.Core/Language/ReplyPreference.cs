namespace ShoppyAI.Core.Language;

/// <summary>Language of ShoppyAI's answers. <see cref="Auto"/> follows the conversation.</summary>
public enum ReplyLanguage
{
    Auto,
    Darija,
    French,
    English,
    StandardArabic
}

/// <summary>Alphabet of Darija answers. <see cref="Auto"/> follows the user's writing.</summary>
public enum ReplyScript
{
    Auto,
    Arabic,
    Latin
}

public enum AnswerLength
{
    Auto,
    Short,
    Detailed
}

/// <summary>How long a reply preference lasts.</summary>
public enum PreferenceScope
{
    /// <summary>Only the current answer ("juste cette fois en anglais").</summary>
    Turn,

    /// <summary>The rest of this conversation ("réponds en français").</summary>
    Conversation,

    /// <summary>Also future conversations ("من اليوم جاوبني بالعربية", "from now on…").</summary>
    Persistent
}

/// <summary>
/// One layer of reply preferences. A null field is "not specified here" and lets a
/// lower layer decide; <c>Auto</c> explicitly asks to follow the user.
/// </summary>
public sealed record ReplyPreference(
    ReplyLanguage? Language = null,
    ReplyScript? Script = null,
    AnswerLength? Length = null)
{
    public static ReplyPreference None { get; } = new();

    public bool IsEmpty => Language is null && Script is null && Length is null;

    /// <summary>This layer's values, completed by <paramref name="lower"/> where this layer has none.</summary>
    public ReplyPreference Over(ReplyPreference lower) =>
        new(Language ?? lower.Language, Script ?? lower.Script, Length ?? lower.Length);

    /// <summary>This layer without the fields that <paramref name="other"/> specifies.</summary>
    public ReplyPreference Without(ReplyPreference other) =>
        new(other.Language is null ? Language : null,
            other.Script is null ? Script : null,
            other.Length is null ? Length : null);
}

/// <summary>A request about the reply language, script or length found in a user message.</summary>
public sealed record LanguageInstruction(
    ReplyPreference Preference,
    PreferenceScope Scope,
    ReplyLanguage? Avoid = null);

/// <summary>Where the reply language of a turn comes from.</summary>
public enum ReplySource
{
    /// <summary>Asked for in the current message.</summary>
    ThisMessage,

    /// <summary>Asked for earlier in this conversation.</summary>
    Conversation,

    /// <summary>Saved preference (sidebar, /lang, or "from now on…").</summary>
    Saved,

    /// <summary>The language the conversation is held in; the latest message alone does not change it.</summary>
    ConversationLanguage,

    /// <summary>The language of the user's message (start of a conversation, or a clear switch).</summary>
    UserMessage
}

/// <summary>What ShoppyAI answers in for one turn, and why.</summary>
/// <param name="Instruction">The request found in the message, completed with the language it implies.</param>
/// <param name="Avoided">A language the user asked not to be answered in.</param>
public sealed record ReplyDecision(
    ReplyLanguage Language,
    ReplyScript Script,
    AnswerLength Length,
    ReplySource Source,
    LanguageInstruction? Instruction,
    ReplyLanguage? Avoided = null)
{
    public string Describe() => (Language, Script) switch
    {
        (ReplyLanguage.Darija, ReplyScript.Arabic) => "Darija (Arabic script)",
        (ReplyLanguage.Darija, _) => "Darija (Latin letters)",
        (ReplyLanguage.French, _) => "French",
        (ReplyLanguage.English, _) => "English",
        (ReplyLanguage.StandardArabic, _) => "Standard Arabic",
        _ => "Auto"
    };
}
