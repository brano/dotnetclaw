using System.Collections.Concurrent;
using DotnetClaw.Agents;
using DotnetClaw.Telegram;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetClaw.Gateway;

// ============================================================================
//  TelegramGatewayAdapter — bridges Telegram polling to the WebSocket gateway
// ============================================================================

/// <summary>
/// Hosted service that bridges the Telegram channel into the WebSocket gateway
/// without a real WebSocket connection — it pushes messages directly through
/// <see cref="GatewayConnectionManager"/>.
///
/// Responsibilities:
///   • Maintains a per-Telegram-chat session ID for agent conversation context.
///   • Accepts inbound Telegram messages via <see cref="InjectTelegramMessageAsync"/>.
///   • Streams <c>agent_chunk</c> / <c>tool_call</c> / <c>tool_result</c> frames to all
///     WebSocket clients in the <c>"telegram"</c> group (real-time observability).
///   • Sends the final response back to the Telegram chat via the bot client.
/// </summary>
public sealed class TelegramGatewayAdapter(
    GatewayConnectionManager connectionManager,
    ClawAgentLoop agentLoop,
    ITelegramBotClient botClient,
    ILogger<TelegramGatewayAdapter> logger) : IHostedService
{
    private readonly ConcurrentDictionary<long, string> _sessions = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("TelegramGatewayAdapter started.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("TelegramGatewayAdapter stopped.");
        return Task.CompletedTask;
    }

    // ── Public bridge API ─────────────────────────────────────────────────────

    /// <summary>
    /// Injects an inbound Telegram message, runs the agent, streams chunks to
    /// the <c>"telegram"</c> WebSocket group for observability, and replies to the chat.
    /// </summary>
    public async Task InjectTelegramMessageAsync(
        long chatId,
        string text,
        int? replyToMessageId = null,
        CancellationToken ct = default)
    {
        var sessionId = _sessions.GetOrAdd(chatId, _ => Guid.NewGuid().ToString("N"));

        logger.LogInformation(
            "TelegramGatewayAdapter: message from chatId={ChatId} session={Session}",
            chatId, sessionId[..8]);

        // Notify observers that a Telegram message arrived
        await connectionManager.SendToGroupAsync("telegram", new GatewayMessage
        {
            Type = MessageType.AgentChunk, SessionId = sessionId, Text = $"[Telegram] {text}"
        }, ct);

        var fullResponse = new System.Text.StringBuilder();

        var renderer = new GatewayRenderer(
            onChunk: async chunk =>
            {
                fullResponse.Append(chunk);
                await connectionManager.SendToGroupAsync("telegram", new GatewayMessage
                {
                    Type = MessageType.AgentChunk, SessionId = sessionId, Text = chunk
                }, ct);
            },
            onToolCall: async (tool, input) =>
            {
                await connectionManager.SendToGroupAsync("telegram", new GatewayMessage
                {
                    Type = MessageType.ToolCall, SessionId = sessionId, Tool = tool, Input = input
                }, ct);
            },
            onToolResult: async (tool, success, preview) =>
            {
                await connectionManager.SendToGroupAsync("telegram", new GatewayMessage
                {
                    Type = MessageType.ToolResult, SessionId = sessionId, Tool = tool, Text = preview
                }, ct);
            });

        try
        {
            await agentLoop.RunTurnAsync(text, ct, renderer);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("TelegramGatewayAdapter: turn cancelled for chatId={ChatId}", chatId);
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TelegramGatewayAdapter: turn failed for chatId={ChatId}", chatId);
            await connectionManager.SendToGroupAsync("telegram", new GatewayMessage
            {
                Type = MessageType.Error, SessionId = sessionId, Text = $"Agent error: {ex.Message}"
            }, ct);
            await botClient.SendMessageAsync(chatId, $"Sorry, an error occurred: {ex.Message}", cancellationToken: ct);
            return;
        }

        var responseText = fullResponse.ToString();
        await connectionManager.SendToGroupAsync("telegram", new GatewayMessage
        {
            Type = MessageType.AgentResponse, SessionId = sessionId, Text = responseText
        }, ct);

        if (!string.IsNullOrWhiteSpace(responseText))
        {
            await botClient.SendMessageAsync(
                chatId, responseText,
                replyToMessageId: replyToMessageId,
                cancellationToken: ct);
        }
    }

    /// <summary>Clears the session for a Telegram chat, forcing a new session on next message.</summary>
    public void ResetSession(long chatId) => _sessions.TryRemove(chatId, out _);
}
