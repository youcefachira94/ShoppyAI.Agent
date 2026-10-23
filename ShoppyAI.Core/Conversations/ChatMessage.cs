namespace ShoppyAI.Core.Conversations;

public enum ChatRole
{
    System,
    User,
    Assistant
}

public sealed record ChatMessage(ChatRole Role, string Content)
{
    public static ChatMessage System(string content) => new(ChatRole.System, content);
    public static ChatMessage User(string content) => new(ChatRole.User, content);
    public static ChatMessage Assistant(string content) => new(ChatRole.Assistant, content);

    /// <summary>Role name expected by the Ollama chat API.</summary>
    public string RoleName => Role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        _ => "assistant"
    };
}
