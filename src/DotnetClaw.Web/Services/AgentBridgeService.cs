using DotnetClaw.Gateway;
using DotnetClaw.Web.Gateway;

namespace DotnetClaw.Web.Services;

// ============================================================================
//  AgentBridgeService — bridges the Blazor Chat UI to the CLI gateway
// ============================================================================

public sealed class AgentBridgeService : IAsyncDisposable
{
    private readonly WebGatewayClientService _gateway;
    private readonly AppState _appState;
    private readonly ChatService _chatService;
    private readonly ILogger<AgentBridgeService> _logger;

    // Unique per-scope session ID used to correlate gateway responses.
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    // Message ID for the currently streaming assistant message.
    private Guid _currentMsgId;
    private TaskCompletionSource? _turnTcs;

    public AgentBridgeService(
        WebGatewayClientService gateway,
        AppState appState,
        ChatService chatService,
        ILogger<AgentBridgeService> logger)
    {
        _gateway     = gateway;
        _appState    = appState;
        _chatService = chatService;
        _logger      = logger;

        _gateway.Subscribe(_sessionId, HandleGatewayMessageAsync);
    }

    public bool IsInitialized => _gateway.IsConnected;

    /// <summary>System prompt is owned by the CLI; this stub keeps the Home page working.</summary>
    public string EffectiveSystemPrompt => _gateway.IsConnected
        ? "(System prompt is managed by the DotnetClaw CLI process.)"
        : string.Empty;

    public Task InitializeAsync(CancellationToken ct = default)
    {
        if (!_gateway.IsConnected)
            _chatService.AddSystemMessage("Connecting to DotnetClaw gateway…");
        else
            _chatService.AddSystemMessage("DotnetClaw gateway connected.");
        return Task.CompletedTask;
    }

    public async Task SendMessageAsync(string userInput, Action<string>? onChunk = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userInput)) return;

        _chatService.AddUserMessage(userInput);
        _appState.SetAgentRunning(true, "Thinking…");

        _currentMsgId = _chatService.BeginAssistantMessage();
        _turnTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Store the onChunk callback so the gateway handler can call it
        _pendingOnChunk = onChunk;

        try
        {
            await _gateway.SendChatMessageAsync(userInput, _sessionId, ct);

            // Wait for agent_response (or error) to arrive via the gateway handler
            using var reg = ct.Register(() => _turnTcs.TrySetCanceled());
            await _turnTcs.Task;
        }
        catch (OperationCanceledException)
        {
            _chatService.FinalizeAssistantMessage(_currentMsgId, "[Cancelled]");
        }
        catch (Exception ex)
        {
            AgentBridgeLog.AgentTurnError(_logger, ex);
            _chatService.FinalizeAssistantMessage(_currentMsgId, $"[Error: {ex.Message}]");
        }
        finally
        {
            _pendingOnChunk = null;
            _appState.SetAgentRunning(false);
        }
    }

    public Task ResetAsync(CancellationToken ct = default)
    {
        _chatService.Clear();
        _chatService.AddSystemMessage("Resetting conversation…");
        return _gateway.SendResetAsync(_sessionId, ct);
    }

    // ── Gateway message handler ───────────────────────────────────────────────

    private Action<string>? _pendingOnChunk;

    private Task HandleGatewayMessageAsync(GatewayMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.AgentChunk:
                var chunk = msg.Text ?? string.Empty;
                _chatService.AppendToAssistantMessage(_currentMsgId, chunk);
                _pendingOnChunk?.Invoke(chunk);
                break;

            case MessageType.AgentResponse:
                _chatService.FinalizeAssistantMessage(_currentMsgId);
                _appState.RecordTurn();
                _turnTcs?.TrySetResult();
                break;

            case MessageType.Error:
                _chatService.FinalizeAssistantMessage(_currentMsgId, $"[Error: {msg.Text ?? "Unknown error"}]");
                _turnTcs?.TrySetResult();
                break;

            case MessageType.ResetSession:
                _chatService.AddSystemMessage("Conversation reset.");
                break;
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _gateway.Unsubscribe(_sessionId);
        await Task.CompletedTask;
    }
}

internal static partial class AgentBridgeLog
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() { }

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error during agent turn")]
    internal static partial void AgentTurnError(ILogger logger, Exception ex);
}
