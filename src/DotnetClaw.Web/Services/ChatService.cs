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
    private readonly Lock _lock = new();
    private readonly List<ChatMessage> _messages = [];
    private readonly AppState _appState;

    public ChatService(AppState appState)
    {
        _appState = appState;
    }

    /// <summary>
    /// Returns a snapshot of the current messages.
    /// A snapshot (not the live list) is returned so that the Blazor render loop
    /// can iterate safely even if the gateway background thread appends a chunk
    /// concurrently.
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages { get { lock (_lock) { return [.._messages]; } } }

    public bool IsThinking { get; private set; }

    public event Action? OnMessagesChanged;

    public void AddUserMessage(string content)
    {
        lock (_lock) _messages.Add(new ChatMessage(Guid.NewGuid(), MessageRole.User, content, DateTime.UtcNow));
        NotifyChanged();
    }

    public Guid BeginAssistantMessage()
    {
        var id = Guid.NewGuid();
        lock (_lock)
        {
            IsThinking = true;
            _messages.Add(new ChatMessage(id, MessageRole.Assistant, string.Empty, DateTime.UtcNow, IsStreaming: true));
        }
        NotifyChanged();
        return id;
    }

    public void AppendToAssistantMessage(Guid id, string chunk)
    {
        lock (_lock)
        {
            var idx = _messages.FindIndex(m => m.Id == id);
            if (idx < 0) return;
            var existing = _messages[idx];
            _messages[idx] = existing with { Content = existing.Content + chunk };
        }
        NotifyChanged();
    }

    public void FinalizeAssistantMessage(Guid id, string? finalContent = null)
    {
        lock (_lock)
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
        }
        NotifyChanged();
    }

    public void AddToolCallMessage(string toolName, string result)
    {
        lock (_lock) _messages.Add(new ChatMessage(Guid.NewGuid(), MessageRole.ToolCall, result, DateTime.UtcNow, ToolName: toolName));
        NotifyChanged();
    }

    public void AddSystemMessage(string content)
    {
        lock (_lock) _messages.Add(new ChatMessage(Guid.NewGuid(), MessageRole.System, content, DateTime.UtcNow));
        NotifyChanged();
    }

    public void Clear()
    {
        lock (_lock) { _messages.Clear(); IsThinking = false; }
        NotifyChanged();
    }

    private void NotifyChanged() => OnMessagesChanged?.Invoke();
}
