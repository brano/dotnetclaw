using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DotnetClaw.Agents;
using DotnetClaw.UI;
using DotnetClaw.Workspace;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DotnetClaw.Gateway;

// ============================================================================
//  GatewayWebSocketHandler — accepts upgrades, runs receive loop, dispatches
// ============================================================================

/// <summary>
/// Handles WebSocket connections for the gateway endpoint.
/// Accepts upgrades, runs a receive loop, and dispatches messages to the agent.
/// </summary>
public sealed class GatewayWebSocketHandler(
    GatewayConnectionManager connectionManager,
    ClawAgentLoop agentLoop,
    ILogger<GatewayWebSocketHandler> logger)
{
    /// <summary>
    /// Accepts a WebSocket upgrade and runs the receive loop until the client disconnects.
    /// </summary>
    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var channel = context.Request.Query["channel"].FirstOrDefault() ?? "web-ui";
        var connectionId = Guid.NewGuid().ToString("N");

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        connectionManager.Add(connectionId, socket, channel);

        try
        {
            await ReceiveLoopAsync(connectionId, socket, context.RequestAborted);
        }
        finally
        {
            connectionManager.Remove(connectionId);
        }
    }

    private async Task ReceiveLoopAsync(string connectionId, WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            using var ms = new MemoryStream();

            try
            {
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
            }
            catch (WebSocketException) { return; }
            catch (OperationCanceledException) { return; }

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            GatewayMessage? message;
            try
            {
                message = JsonSerializer.Deserialize(
                    Encoding.UTF8.GetString(ms.ToArray()),
                    GatewayJsonContext.Default.GatewayMessage);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Gateway: invalid JSON from {Id}", connectionId[..8]);
                continue;
            }

            if (message is not null)
                await DispatchAsync(connectionId, message, ct);
        }
    }

    private async Task DispatchAsync(string connectionId, GatewayMessage message, CancellationToken ct)
    {
        switch (message.Type)
        {
            case MessageType.ChatMessage:
                await HandleChatMessageAsync(connectionId, message.SessionId ?? "", message.Text ?? "", ct);
                break;

            case MessageType.ResetSession:
                await HandleResetSessionAsync(connectionId, message.SessionId ?? "", ct);
                break;

            default:
                logger.LogWarning(
                    "Gateway: unknown message type '{Type}' from {Id}",
                    message.Type, connectionId[..8]);
                break;
        }
    }

    private async Task HandleChatMessageAsync(
        string connectionId, string sessionId, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await connectionManager.SendAsync(connectionId, new GatewayMessage
            {
                Type = MessageType.Error, SessionId = sessionId, Text = "text must not be empty"
            }, ct);
            return;
        }

        logger.LogInformation(
            "Gateway: chat_message session={Session} from {Id}",
            sessionId[..Math.Min(8, sessionId.Length)], connectionId[..8]);

        var fullResponse = new StringBuilder();

        var renderer = new GatewayRenderer(
            onChunk: async chunk =>
            {
                fullResponse.Append(chunk);
                await connectionManager.SendAsync(connectionId, new GatewayMessage
                {
                    Type = MessageType.AgentChunk, SessionId = sessionId, Text = chunk
                }, ct);
            },
            onToolCall: async (tool, input) =>
            {
                await connectionManager.SendAsync(connectionId, new GatewayMessage
                {
                    Type = MessageType.ToolCall, SessionId = sessionId, Tool = tool, Input = input
                }, ct);
            },
            onToolResult: async (tool, _, preview) =>
            {
                await connectionManager.SendAsync(connectionId, new GatewayMessage
                {
                    Type = MessageType.ToolResult, SessionId = sessionId, Tool = tool, Text = preview
                }, ct);
            });

        try
        {
            await agentLoop.RunTurnAsync(text, ct, renderer);
            await connectionManager.SendAsync(connectionId, new GatewayMessage
            {
                Type = MessageType.AgentResponse, SessionId = sessionId, Text = fullResponse.ToString()
            }, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Gateway: agent turn failed for session {Session}", sessionId);
            await connectionManager.SendAsync(connectionId, new GatewayMessage
            {
                Type = MessageType.Error, SessionId = sessionId, Text = $"Agent error: {ex.Message}"
            }, ct);
        }
    }

    private async Task HandleResetSessionAsync(string connectionId, string sessionId, CancellationToken ct)
    {
        logger.LogInformation(
            "Gateway: reset_session session={Session} from {Id}",
            sessionId[..Math.Min(8, sessionId.Length)], connectionId[..8]);
        try
        {
            await agentLoop.ResetAsync(ct);
            await connectionManager.SendAsync(connectionId, new GatewayMessage
            {
                Type = MessageType.ResetSession, SessionId = sessionId
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Gateway: reset_session failed");
            await connectionManager.SendAsync(connectionId, new GatewayMessage
            {
                Type = MessageType.Error, SessionId = sessionId, Text = $"Reset failed: {ex.Message}"
            }, ct);
        }
    }
}

// ============================================================================
//  GatewayRenderer — IConsoleRenderer that pushes to WebSocket callbacks
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
