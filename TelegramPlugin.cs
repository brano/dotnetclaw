using System.ComponentModel;
using DotnetClaw.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace DotnetClaw.Plugins;

/// <summary>
/// Telegram skill — lets DotnetClaw proactively push messages to a Telegram chat
/// during long-running tasks, without waiting for a user prompt.
///
/// Example uses:
///   • "When the build succeeds, send me a Telegram message"
///   • "Notify me on Telegram if any tests fail"
///   • "Keep me posted on Telegram as you refactor each file"
/// </summary>
public sealed class TelegramPlugin(
    DotnetClaw.Telegram.ITelegramBotClient botClient,
    IOptions<TelegramOptions> options,
    ILogger<TelegramPlugin> logger)
{
    private readonly TelegramOptions _options = options.Value;

    [KernelFunction("send_telegram_message")]
    [Description(
        "Send a text message to a Telegram chat. " +
        "Use this to proactively notify the user of progress, results, or errors during long tasks. " +
        "If no chatId is provided, the first configured AllowedChatId is used.")]
    public async Task<string> SendMessageAsync(
        [Description("The message text to send. Supports plain text or Markdown.")]
        string message,

        [Description(
            "Optional: target Telegram chat ID. " +
            "Defaults to the first AllowedChatId in configuration.")]
        long? chatId = null,

        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || !_options.IsConfigured)
            return "[SKIP] Telegram is disabled or not configured.";

        var targetChat = chatId ?? _options.AllowedChatIds.FirstOrDefault();
        if (targetChat == 0)
            return "[ERROR] No target chat ID available. Add AllowedChatIds to configuration.";

        if (!_options.AllowedChatIds.Contains(targetChat))
        {
            logger.LogWarning("Blocked send_telegram_message to non-allowed chat {Id}", targetChat);
            return $"[BLOCKED] Chat {targetChat} is not in the AllowedChatIds list.";
        }

        logger.LogInformation("Sending Telegram message to chat {Id}: {Preview}",
            targetChat, message[..Math.Min(60, message.Length)]);

        var sent = await botClient.SendMessageAsync(
            targetChat, message,
            parseMode: _options.ParseMode,
            cancellationToken: cancellationToken);

        return sent is not null
            ? $"[OK] Message sent to chat {targetChat} (message_id={sent.MessageId})"
            : $"[ERROR] Failed to send message to chat {targetChat}.";
    }

    [KernelFunction("send_telegram_notification")]
    [Description(
        "Send a short status notification to the default Telegram chat. " +
        "Wraps the text in a bold header for visibility. " +
        "Use for quick status updates like 'Build succeeded' or 'Tests passed'.")]
    public async Task<string> SendNotificationAsync(
        [Description("Short notification title, e.g. 'Build Complete' or 'Tests Failed'")]
        string title,

        [Description("Optional detail text shown below the title.")]
        string? details = null,

        CancellationToken cancellationToken = default)
    {
        var escaped = DotnetClaw.Telegram.TelegramCommandRouter.EscapeMarkdown(title);
        var body = string.IsNullOrWhiteSpace(details)
            ? $"🔔 *{escaped}*"
            : $"🔔 *{escaped}*\n\n{DotnetClaw.Telegram.TelegramCommandRouter.EscapeMarkdown(details)}";

        return await SendMessageAsync(body, cancellationToken: cancellationToken);
    }
}
