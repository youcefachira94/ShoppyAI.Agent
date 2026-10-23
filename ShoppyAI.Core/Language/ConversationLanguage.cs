namespace ShoppyAI.Core.Language;

/// <summary>
/// Decides the reply language of each turn and keeps the language of one conversation.
/// <list type="bullet">
/// <item>An explicit request wins: for this answer only, for the conversation, or saved.</item>
/// <item>Otherwise the conversation keeps its language: one message in another language
/// (an English technical question in a Darija chat) does not switch it, and neither does a
/// short or unclear message ("oui", "vasy", "saha"). Two clear messages in a row in another
/// language do: the user can always change language naturally.</item>
/// </list>
/// </summary>
public sealed class ConversationLanguage
{
    private const int MessagesToSwitch = 2;

    private ReplyLanguage? _established;

    /// <summary>True while the language only comes from a short or unclear message ("hello", "bonjour").</summary>
    private bool _establishedIsTentative;
    private ReplyLanguage? _switchCandidate;
    private int _switchVotes;
    private ReplyScript? _lastDarijaScript;

    /// <summary>Preferences asked for in this conversation; they last until a new chat.</summary>
    public ReplyPreference Requested { get; private set; } = ReplyPreference.None;

    /// <summary>A language the user asked not to be answered in ("pas en anglais").</summary>
    public ReplyLanguage? Avoided { get; private set; }

    /// <summary>The language the conversation is held in, as followed automatically.</summary>
    public ReplyLanguage? Established => _established;

    public void Reset()
    {
        _established = null;
        _establishedIsTentative = false;
        _switchCandidate = null;
        _switchVotes = 0;
        _lastDarijaScript = null;
        Requested = ReplyPreference.None;
        Avoided = null;
    }

    /// <summary>
    /// Forgets the requests made in this conversation. With <paramref name="fields"/>, only
    /// the fields it specifies are forgotten and the conversation keeps its language.
    /// </summary>
    public void ResetRequests(ReplyPreference? fields = null)
    {
        Requested = fields is null ? ReplyPreference.None : Requested.Without(fields);
        if (fields is null || fields.Language is not null)
            Avoided = null;

        // A full reset also lets the next message set the language again.
        if (fields is null)
            (_established, _establishedIsTentative, _switchCandidate, _switchVotes) = (null, false, null, 0);
    }

    /// <summary>Computes the reply for one message without changing any state.</summary>
    public ReplyDecision Decide(LanguageDetection detection, LanguageInstruction? instruction, ReplyPreference saved)
    {
        var (turn, conversation, persistent) = Layers(instruction, saved);
        var fromMessage = instruction?.Preference ?? ReplyPreference.None;

        ReplyLanguage language;
        ReplySource source;

        if (turn.Language is { } turnLanguage and not ReplyLanguage.Auto)
            (language, source) = (turnLanguage, ReplySource.ThisMessage);
        else if (fromMessage.Language is ReplyLanguage.Auto)
            (language, source) = (Map(detection.Language) ?? _established ?? ReplyLanguage.Darija, ReplySource.ThisMessage);
        else if (turn.Language is ReplyLanguage.Auto || conversation.Language is ReplyLanguage.Auto)
            (language, source) = FollowUser(detection, immediately: true);
        else if (conversation.Language is { } conversationLanguage)
            (language, source) = (conversationLanguage, fromMessage.Language is null ? ReplySource.Conversation : ReplySource.ThisMessage);
        else if (persistent.Language is { } savedLanguage and not ReplyLanguage.Auto)
            (language, source) = (savedLanguage, instruction?.Scope == PreferenceScope.Persistent && fromMessage.Language is not null
                ? ReplySource.ThisMessage
                : ReplySource.Saved);
        else
            (language, source) = FollowUser(detection);

        // "Write in Arabic letters" / "ekteb b arabizi" without a language: Darija, unless already Standard
        // Arabic in Arabic letters. The language is then kept with the alphabet, so that later messages
        // in English or French do not lose it.
        if (instruction is not null && fromMessage.Language is null && fromMessage.Script is ReplyScript.Arabic or ReplyScript.Latin)
        {
            // Standard Arabic is kept only when it was chosen or clearly used, not guessed from one phrase.
            var keepStandardArabic = language == ReplyLanguage.StandardArabic && fromMessage.Script == ReplyScript.Arabic &&
                                     (source != ReplySource.UserMessage || !detection.IsWeak);
            if (!keepStandardArabic)
                language = ReplyLanguage.Darija;

            source = ReplySource.ThisMessage;
            instruction = instruction with { Preference = fromMessage with { Language = language } };
        }

        var avoided = instruction is null ? Avoided : instruction.Avoid ?? (instruction.Preference.Language is null ? Avoided : null);
        if (avoided == language)
            language = language == ReplyLanguage.Darija ? ReplyLanguage.French : ReplyLanguage.Darija;

        // A layer saying Auto stops the layers below it ("reply in my language" beats a saved alphabet).
        var script = language switch
        {
            ReplyLanguage.Darija => FirstSpecified(turn.Script, conversation.Script, persistent.Script) is { } chosen and not ReplyScript.Auto
                ? chosen
                : FollowUserScript(detection),
            ReplyLanguage.StandardArabic => ReplyScript.Arabic,
            _ => ReplyScript.Latin
        };

        var length = FirstSpecified(turn.Length, conversation.Length, persistent.Length) ?? AnswerLength.Auto;
        return new ReplyDecision(language, script, length, source, instruction, avoided);
    }

    /// <summary>Records a completed turn: the requests it contained and the language the user wrote in.</summary>
    public void Commit(LanguageDetection detection, LanguageInstruction? instruction)
    {
        if (instruction is not null)
        {
            switch (instruction.Scope)
            {
                case PreferenceScope.Conversation:
                    Requested = instruction.Preference.Over(Requested);
                    break;
                case PreferenceScope.Persistent:
                    // The saved preference now decides; earlier requests of this chat must not hide it.
                    Requested = Requested.Without(instruction.Preference);
                    break;
            }

            if (instruction.Scope != PreferenceScope.Turn)
            {
                if (instruction.Avoid is not null)
                    Avoided = instruction.Avoid;
                else if (instruction.Preference.Language is not null)
                    Avoided = null;
            }
        }

        if (instruction is null)
        {
            Follow(detection, immediately: Requested.Language is ReplyLanguage.Auto);
            return;
        }

        // A message carrying a request does not count as a vote to switch language; an explicit
        // language becomes the conversation's language, and "réponds dans ma langue" takes this message's.
        var chosen = instruction.Scope == PreferenceScope.Turn ? null : instruction.Preference.Language switch
        {
            ReplyLanguage.Auto => Map(detection.Language),
            { } language => language,
            null => null
        };

        if (chosen is not null)
        {
            _established = chosen;
            (_establishedIsTentative, _switchCandidate, _switchVotes) = (false, null, 0);
        }

        TrackDarijaScript(detection);
    }

    /// <summary>The three preference layers for this turn, with the message's request in its place.</summary>
    private (ReplyPreference Turn, ReplyPreference Conversation, ReplyPreference Persistent) Layers(
        LanguageInstruction? instruction, ReplyPreference saved)
    {
        var turn = ReplyPreference.None;
        var conversation = Requested;
        var persistent = saved;

        switch (instruction?.Scope)
        {
            case PreferenceScope.Turn:
                turn = instruction.Preference;
                break;
            case PreferenceScope.Conversation:
                conversation = instruction.Preference.Over(Requested);
                break;
            case PreferenceScope.Persistent:
                conversation = Requested.Without(instruction.Preference);
                persistent = instruction.Preference.Over(saved);
                break;
        }

        return (turn, conversation, persistent);
    }

    /// <param name="immediately">
    /// The user asked to be answered in their language ("réponds dans ma langue"): a clear message
    /// switches at once instead of after two messages.
    /// </param>
    private (ReplyLanguage Language, ReplySource Source) FollowUser(LanguageDetection detection, bool immediately = false)
    {
        var language = Map(detection.Language);

        if (_established is not { } established)
            return (language ?? ReplyLanguage.Darija, ReplySource.UserMessage);

        if (detection.IsWeak || language is null || language == established)
            return (established, language == established ? ReplySource.UserMessage : ReplySource.ConversationLanguage);

        return immediately || _establishedIsTentative || _switchCandidate == language && _switchVotes + 1 >= MessagesToSwitch
            ? (language.Value, ReplySource.UserMessage)
            : (established, ReplySource.ConversationLanguage);
    }

    private ReplyScript FollowUserScript(LanguageDetection detection)
    {
        if (Map(detection.Language) == ReplyLanguage.Darija && detection.Script != WritingScript.None)
            return detection.Script == WritingScript.Arabic ? ReplyScript.Arabic : ReplyScript.Latin;

        return _lastDarijaScript ?? (detection.Script == WritingScript.Arabic ? ReplyScript.Arabic : ReplyScript.Latin);
    }

    private void TrackDarijaScript(LanguageDetection detection)
    {
        if (Map(detection.Language) == ReplyLanguage.Darija && detection.Script != WritingScript.None)
            _lastDarijaScript = detection.Script == WritingScript.Arabic ? ReplyScript.Arabic : ReplyScript.Latin;
    }

    private void Follow(LanguageDetection detection, bool immediately)
    {
        TrackDarijaScript(detection);
        var language = Map(detection.Language);

        if (language is null)
            return;

        if (_established is null)
        {
            _established = language;
            _establishedIsTentative = detection.IsWeak;
            return;
        }

        if (detection.IsWeak || language == _established)
        {
            if (language == _established)
            {
                _establishedIsTentative &= detection.IsWeak;
                (_switchCandidate, _switchVotes) = (null, 0);
            }

            return;
        }

        _switchVotes = _switchCandidate == language ? _switchVotes + 1 : 1;
        _switchCandidate = language;

        // A clear message replaces a tentative language at once ("hello", then a Darija question).
        if (immediately || _establishedIsTentative || _switchVotes >= MessagesToSwitch)
        {
            _established = language;
            (_establishedIsTentative, _switchCandidate, _switchVotes) = (false, null, 0);
        }
    }

    private static T? FirstSpecified<T>(T? first, T? second, T? third) where T : struct => first ?? second ?? third;

    private static ReplyLanguage? Map(DetectedLanguage language) => language switch
    {
        DetectedLanguage.Darija or DetectedLanguage.ArabicUnspecified => ReplyLanguage.Darija,
        DetectedLanguage.StandardArabic => ReplyLanguage.StandardArabic,
        DetectedLanguage.French => ReplyLanguage.French,
        DetectedLanguage.English => ReplyLanguage.English,
        _ => null
    };
}
