namespace ShoppyAI.Core.Conversations;

/// <summary>
/// Keeps the most recent part of a conversation that fits in the model context, in
/// order, never starting with an assistant message.
/// </summary>
public static class ContextWindow
{
    /// <summary>Rough token estimate. Arabic uses more tokens per character than Latin text, so stay conservative.</summary>
    public static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length / 2.5) + 4;

    public static (IReadOnlyList<ChatMessage> Messages, int Dropped) SelectRecent(
        IReadOnlyList<ChatMessage> history, int tokenBudget)
    {
        var start = history.Count;
        var used = 0;

        while (start > 0)
        {
            var cost = EstimateTokens(history[start - 1].Content);
            if (used + cost > tokenBudget)
                break;

            used += cost;
            start--;
        }

        while (start < history.Count && history[start].Role != ChatRole.User)
            start++;

        return (history.Skip(start).ToList(), start);
    }
}
