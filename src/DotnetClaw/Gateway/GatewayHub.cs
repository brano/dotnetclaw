using System.Text;
using DotnetClaw.Agents;
using DotnetClaw.UI;
using DotnetClaw.Workspace;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace DotnetClaw.Gateway;

// ============================================================================
//  GatewayHub — SignalR hub for all real-time gateway communication
// ============================================================================

/// <summary>
/// SignalR hub that multiplexes agent communication across Web, CLI and Telegram
/// channels.
///
/// Channel management:
///   Clients pass <c>?channel=web-ui</c> (or <c>cli</c> / <c>telegram</c>) in the
///   connection query string. <see cref="OnConnectedAsync"/> adds the connection to
///   the corresponding SignalR group so the Telegram adapter can broadcast to all
///   observers of a channel without knowing individual connection IDs.
///
/// Session routing:
///   Every hub method receives a <paramref name="sessionId"/> supplied by the caller.
///   The server echoes this id back in all response messages, allowing Web clients
///   that share one SignalR connection across multiple Blazor scopes to demultiplex
///   responses to the correct component.
/// </summary>
public sealed class GatewayHub(
    ClawAgentLoop agentLoop,
    ILogger<GatewayHub> logger) : Hub<IGatewayClient>
{
    // ── Connection lifecycle ──────────────────────────────────────────────────

    public override async Task OnConnectedAsync()
    {
        var channel = Context.GetHttpContext()?.Request.Query["channel"].FirstOrDefault() ?? "web-ui";
        await Groups.AddToGroupAsync(Context.ConnectionId, channel);
        logger.LogInformation(
            "Gateway: {Id} connected on channel '{Channel}'",
            Context.ConnectionId[..8], channel);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogInformation("Gateway: {Id} disconnected", Context.ConnectionId[..8]);
        await base.OnDisconnectedAsync(exception);
    }

    // ── Client-invokable methods ──────────────────────────────────────────────

    /// <summary>
    /// Routes a user message to the agent loop and streams the response back to
    /// the caller via <see cref="IGatewayClient"/> methods.
    /// </summary>
    public async Task SendChatMessage(string sessionId, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await Clients.Caller.ReceiveError(sessionId, "text must not be empty");
            return;
        }

        logger.LogInformation(
            "Gateway: chat_message session={Session} from {Id}",
            sessionId[..Math.Min(8, sessionId.Length)], Context.ConnectionId[..8]);

        var caller = Clients.Caller;
        var fullResponse = new StringBuilder();
        var ct = Context.ConnectionAborted;

        var renderer = new GatewayRenderer(
            onChunk: async chunk =>
            {
                fullResponse.Append(chunk);
                await caller.ReceiveChunk(sessionId, chunk);
            },
            onToolCall: async (tool, input) =>
                await caller.ReceiveToolCall(sessionId, tool, input),
            onToolResult: async (tool, _, preview) =>
                await caller.ReceiveToolResult(sessionId, tool, preview));

        try
        {
            await agentLoop.RunTurnAsync(text, ct, renderer);
            await caller.ReceiveAgentResponse(sessionId, fullResponse.ToString());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Gateway: agent turn failed for session {Session}", sessionId);
            await caller.ReceiveError(sessionId, $"Agent error: {ex.Message}");
        }
    }

    /// <summary>Resets the agent conversation and sends a confirmation to the caller.</summary>
    public async Task ResetSession(string sessionId)
    {
        logger.LogInformation(
            "Gateway: reset_session session={Session} from {Id}",
            sessionId[..Math.Min(8, sessionId.Length)], Context.ConnectionId[..8]);
        try
        {
            await agentLoop.ResetAsync(Context.ConnectionAborted);
            await Clients.Caller.OnSessionReset(sessionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Gateway: reset_session failed");
            await Clients.Caller.ReceiveError(sessionId, $"Reset failed: {ex.Message}");
        }
    }
}

// ============================================================================
//  GatewayRenderer — IConsoleRenderer that pushes to IGatewayClient callbacks
// ============================================================================

internal sealed class GatewayRenderer(
    Func<string, Task> onChunk,
    Func<string, string, Task> onToolCall,
    Func<string, bool, string, Task> onToolResult) : IConsoleRenderer
{
    public void BeginAssistantTurn() { }
    public void EndAssistantTurn() { }

    public void WriteChunk(string text) => _ = onChunk(text);
    public void WriteToolCall(string toolName, string input) => _ = onToolCall(toolName, input);
    public void WriteToolResult(string toolName, bool success, string preview) => _ = onToolResult(toolName, success, preview);

    public void WriteWarning(string message) { }
    public void WriteError(string message) { }
    public void WriteBanner() { }
    public void WriteWorkspaceStatus(WorkspaceLoadResult result) { }
    public string PromptUser(string prompt = "> ") => string.Empty;
}
