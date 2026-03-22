namespace DotnetClaw.Web.Services;

// ============================================================================
//  ChatService — manages chat history for the web UI
// ============================================================================

public enum MessageRole { User, Assistant, System, ToolCall }

public sealed record ChatMessage(
    Guid Id,
    MessageRole Role,
    string Content,
    DateTime Timestamp,
    string? ToolName = null,
    bool IsStreaming = false
);

public sealed class ChatService
{
    private readonly List<ChatMessage> _messages = [];
    private readonly AppState _appState;

    public ChatService(AppState appState)
    {
        _appState = appState;
    }

    public IReadOnlyList<ChatMessage> Messages => _messages.AsReadOnly();
    public bool IsThinking { get; private set; }

    public event Action? OnMessagesChanged;

    public void AddUserMessage(string content)
    {
        _messages.Add(new ChatMessage(Guid.NewGuid(), MessageRole.User, content, DateTime.UtcNow));
        NotifyChanged();
    }

    public Guid BeginAssistantMessage()
    {
        IsThinking = true;
        var id = Guid.NewGuid();
        _messages.Add(new ChatMessage(id, MessageRole.Assistant, string.Empty, DateTime.UtcNow, IsStreaming: true));
        NotifyChanged();
        return id;
    }

    public void AppendToAssistantMessage(Guid id, string chunk)
    {
        var idx = _messages.FindIndex(m => m.Id == id);
        if (idx < 0) return;
        var existing = _messages[idx];
        _messages[idx] = existing with { Content = existing.Content + chunk };
        NotifyChanged();
    }

    public void FinalizeAssistantMessage(Guid id, string? finalContent = null)
    {
        IsThinking = false;
        var idx = _messages.FindIndex(m => m.Id == id);
        if (idx < 0) return;
        var existing = _messages[idx];
        _messages[idx] = existing with
        {
            Content = finalContent ?? existing.Content,
            IsStreaming = false,
        };
        NotifyChanged();
    }

    public void AddToolCallMessage(string toolName, string result)
    {
        _messages.Add(new ChatMessage(Guid.NewGuid(), MessageRole.ToolCall, result, DateTime.UtcNow, ToolName: toolName));
        NotifyChanged();
    }

    public void AddSystemMessage(string content)
    {
        _messages.Add(new ChatMessage(Guid.NewGuid(), MessageRole.System, content, DateTime.UtcNow));
        NotifyChanged();
    }

    public void Clear()
    {
        _messages.Clear();
        IsThinking = false;
        NotifyChanged();
    }

    private void NotifyChanged() => OnMessagesChanged?.Invoke();
}
