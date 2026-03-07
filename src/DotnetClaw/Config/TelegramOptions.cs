namespace DotnetClaw.Config;

/// <summary>
/// Configuration for the Telegram Bot integration.
/// Bound from <c>appsettings.json</c> under the <c>DotnetClaw:Telegram</c> key.
///
/// Obtain a bot token by messaging @BotFather on Telegram and running /newbot.
/// Find your chat ID by messaging @userinfobot.
/// </summary>
public sealed class TelegramOptions
{
    /// <summary>
    /// Telegram Bot API token from @BotFather. Format: "123456789:ABCDefGhIJKlmNoPQRsTUVwxyZ".
    /// Can also be set via the TELEGRAM_BOT_TOKEN environment variable (recommended for prod).
    /// </summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>
    /// Whitelist of Telegram chat IDs allowed to control DotnetClaw.
    /// An empty list BLOCKS all messages — you must add at least one chat ID.
    /// Both user IDs (private chats) and group chat IDs (negative numbers) are supported.
    /// </summary>
    public List<long> AllowedChatIds { get; set; } = [];

    /// <summary>
    /// Whether the Telegram bot integration is enabled at all.
    /// Set to false to disable without removing the configuration.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Long-polling timeout in seconds passed to Telegram's getUpdates endpoint.
    /// Higher values reduce API requests; lower values increase responsiveness.
    /// Telegram recommends 20–60 seconds.
    /// </summary>
    public int LongPollTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of updates to fetch per getUpdates call.
    /// Range 1–100. Default 10.
    /// </summary>
    public int MaxUpdatesPerPoll { get; set; } = 10;

    /// <summary>
    /// Telegram message hard limit is 4096 UTF-16 code units.
    /// DotnetClaw splits responses exceeding this length into multiple messages.
    /// </summary>
    public int MaxMessageLength { get; set; } = 4000; // slightly under 4096 for safety

    /// <summary>
    /// Parse mode for outgoing messages. "MarkdownV2" or "HTML" or "".
    /// DotnetClaw uses MarkdownV2 by default for code block formatting.
    /// </summary>
    public string ParseMode { get; set; } = "MarkdownV2";

    /// <summary>
    /// When true, DotnetClaw sends a typing indicator (sendChatAction) while
    /// processing a request — gives the user visual feedback.
    /// </summary>
    public bool SendTypingIndicator { get; set; } = true;

    /// <summary>
    /// Effective bot token — prefers the TELEGRAM_BOT_TOKEN env var over appsettings.
    /// </summary>
    public string EffectiveBotToken =>
        Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
        ?? BotToken;

    /// <summary>Returns true only when a non-empty token is configured.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(EffectiveBotToken) && AllowedChatIds.Count > 0;
}
