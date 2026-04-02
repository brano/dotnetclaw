using System.Collections.Concurrent;
using DotnetClaw.Agents;
using DotnetClaw.Telegram;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetClaw.Gateway;

// ============================================================================
//  TelegramGatewayAdapter — bridges Telegram polling to the SignalR hub
// ============================================================================

/// <summary>
/// Hosted service that bridges the Telegram channel into the SignalR
/// <see cref="GatewayHub"/> without a real WebSocket connection — it pushes
/// messages directly through <see cref="IHubContext{THub,T}"/>.
///
/// Responsibilities:
///   • Maintains a per-Telegram-chat session ID for agent conversation context.
///   • Accepts inbound Telegram messages via <see cref="InjectTelegramMessageAsync"/>.
///   • Streams <c>agent_chunk</c> / <c>tool_call</c> / <c>tool_result</c> frames to all
///     SignalR clients in the <c>"telegram"</c> group (real-time observability).
///   • Sends the final response back to the Telegram chat via the bot client.
/// </summary>
public sealed class TelegramGatewayAdapter(
    IHubContext<GatewayHub, IGatewayClient> hubContext,
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
    /// the <c>"telegram"</c> SignalR group for observability, and replies to the chat.
    /// </summary>
    public async Task InjectTelegramMessageAsync(
        long chatId,
        string text,
        int? replyToMessageId = null,
        CancellationToken ct = default)
    {
        var sessionId = _sessions.GetOrAdd(chatId, _ => Guid.NewGuid().ToString("N"));
        var telegramGroup = hubContext.Clients.Group("telegram");

        logger.LogInformation(
            "TelegramGatewayAdapter: message from chatId={ChatId} session={Session}",
            chatId, sessionId[..8]);

        // Notify observers that a Telegram message arrived
        await telegramGroup.ReceiveChunk(sessionId, $"[Telegram] {text}");

        var fullResponse = new System.Text.StringBuilder();

        var renderer = new GatewayRenderer(
            onChunk: async chunk =>
            {
                fullResponse.Append(chunk);
                await telegramGroup.ReceiveChunk(sessionId, chunk);
            },
            onToolCall: async (tool, input) =>
                await telegramGroup.ReceiveToolCall(sessionId, tool, input),
            onToolResult: async (tool, success, preview) =>
                await telegramGroup.ReceiveToolResult(sessionId, tool, preview));

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
            await telegramGroup.ReceiveError(sessionId, $"Agent error: {ex.Message}");
            await botClient.SendMessageAsync(chatId, $"Sorry, an error occurred: {ex.Message}", cancellationToken: ct);
            return;
        }

        var responseText = fullResponse.ToString();
        await telegramGroup.ReceiveAgentResponse(sessionId, responseText);

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
