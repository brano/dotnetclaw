using DotnetClaw.Agents;
using DotnetClaw.UI;
using DotnetClaw.Workspace;

namespace DotnetClaw.Web.Services;

// ============================================================================
//  CapturingRenderer — IConsoleRenderer that streams tokens to a callback
// ============================================================================

internal sealed class CapturingRenderer(Action<string> onChunk) : IConsoleRenderer
{
    private readonly System.Text.StringBuilder _buffer = new();

    public string CapturedText => _buffer.ToString();

    public void BeginAssistantTurn() { }
    public void WriteChunk(string text) { _buffer.Append(text); onChunk(text); }
    public void EndAssistantTurn() { }
    public void WriteWarning(string message) { }
    public void WriteToolCall(string toolName, string input) { }
    public void WriteToolResult(string toolName, bool success, string preview) { }
    public void WriteError(string message) { }
    public void WriteBanner() { }
    public void WriteWorkspaceStatus(WorkspaceLoadResult result) { }
    public string PromptUser(string prompt = "> ") => string.Empty;
}

// ============================================================================
//  AgentBridgeService — bridges the Blazor UI to the ClawAgentLoop
// ============================================================================

public sealed class AgentBridgeService
{
    private readonly ClawAgentLoop _agentLoop;
    private readonly AppState _appState;
    private readonly ChatService _chatService;
    private readonly ILogger<AgentBridgeService> _logger;

    public AgentBridgeService(
        ClawAgentLoop agentLoop,
        AppState appState,
        ChatService chatService,
        ILogger<AgentBridgeService> logger)
    {
        _agentLoop = agentLoop;
        _appState = appState;
        _chatService = chatService;
        _logger = logger;
    }

    public bool IsInitialized { get; private set; }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (IsInitialized) return;
        await _agentLoop.InitialiseAsync(ct);
        IsInitialized = true;
        _chatService.AddSystemMessage("DotnetClaw agent initialized. Workspace loaded.");
    }

    public async Task SendMessageAsync(string userInput, Action<string>? onChunk = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userInput)) return;

        _chatService.AddUserMessage(userInput);
        _appState.SetAgentRunning(true, "Thinking…");

        var assistantMsgId = _chatService.BeginAssistantMessage();

        try
        {
            var renderer = new CapturingRenderer(chunk =>
            {
                _chatService.AppendToAssistantMessage(assistantMsgId, chunk);
                onChunk?.Invoke(chunk);
            });

            await _agentLoop.RunTurnAsync(userInput, ct, renderer);
            _chatService.FinalizeAssistantMessage(assistantMsgId);
            _appState.RecordTurn();
        }
        catch (OperationCanceledException)
        {
            _chatService.FinalizeAssistantMessage(assistantMsgId, "[Cancelled]");
        }
        catch (Exception ex)
        {
            AgentBridgeLog.AgentTurnError(_logger, ex);
            _chatService.FinalizeAssistantMessage(assistantMsgId, $"[Error: {ex.Message}]");
        }
        finally
        {
            _appState.SetAgentRunning(false);
        }
    }

    public async Task ResetAsync(CancellationToken ct = default)
    {
        await _agentLoop.ResetAsync(ct);
        _chatService.Clear();
        _chatService.AddSystemMessage("Conversation reset. Workspace reloaded.");
    }

    public string EffectiveSystemPrompt => _agentLoop.EffectiveSystemPrompt;
}

internal static partial class AgentBridgeLog
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() { }

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error during agent turn")]
    internal static partial void AgentTurnError(ILogger logger, Exception ex);
}
