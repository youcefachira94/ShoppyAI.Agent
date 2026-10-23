namespace ShoppyAI.Core.Workspace;

public sealed class WorkspaceAccessException(string message, Exception? inner = null)
    : IOException(message, inner);
