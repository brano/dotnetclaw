using DotnetClaw.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetClaw.Telegram;

/// <summary>
/// Background service that long-polls the Telegram Bot API for new messages
/// and dispatches them to <see cref="TelegramCommandRouter"/>.
///
/// Design:
///   • Uses getUpdates long-polling (no webhook required — works behind NAT/firewalls)
///   • Tracks <c>offset</c> to acknowledge each update exactly once
///   • Enforces the <c>AllowedChatIds</c> whitelist before any processing
///   • Sends a typing indicator while the agent is thinking
///   • Serialises concurrent requests per-chat with a <see cref="SemaphoreSlim"/>
///     so two messages from the same chat never interleave agent calls
/// </summary>
public sealed class TelegramPollingService(
    ITelegramBotClient botClient,
    TelegramCommandRouter router,
    IOptions<TelegramOptions> options,
    ILogger<TelegramPollingService> logger) : IHostedService, IDisposable
{
    private readonly TelegramOptions _options = options.Value;

    // One semaphore per chat — prevents interleaved responses
    private readonly Dictionary<long, SemaphoreSlim> _chatLocks = new();
    private readonly object _lockMap = new();

    private CancellationTokenSource? _cts;
    private Task? _pollLoop;

    // ── IHostedService ────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !_options.IsConfigured)
        {
            logger.LogInformation(
                "Telegram bot is disabled or not configured. " +
                "Set Enabled=true and provide BotToken + AllowedChatIds to activate.");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollLoop = Task.Run(() => RunPollLoopAsync(_cts.Token), _cts.Token);

        logger.LogInformation(
            "Telegram polling service started. AllowedChats={Chats}",
            string.Join(", ", _options.AllowedChatIds));

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping Telegram polling service…");
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        if (_pollLoop is not null)
        {
            try { await _pollLoop.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { /* expected */ }
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
        foreach (var sem in _chatLocks.Values) sem.Dispose();
    }

    // ── Polling loop ──────────────────────────────────────────────────────────

    private async Task RunPollLoopAsync(CancellationToken cancellationToken)
    {
        // Verify the bot token works before entering the loop
        var me = await botClient.GetMeAsync(cancellationToken);
        if (me is null)
        {
            logger.LogError("Failed to connect to Telegram. Check BotToken. Polling will not start.");
            return;
        }

        logger.LogInformation("🤖 Telegram bot @{Username} ({Id}) connected.", me.Username, me.Id);

        long offset = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var updates = await botClient.GetUpdatesAsync(
                    offset: offset,
                    limit: _options.MaxUpdatesPerPoll,
                    timeoutSeconds: _options.LongPollTimeoutSeconds,
                    cancellationToken: cancellationToken);

                foreach (var update in updates)
                {
                    // Advance offset BEFORE processing so a crash doesn't re-deliver
                    offset = update.UpdateId + 1;

                    // Fire-and-forget per-update handling (don't block the poll loop)
                    _ = HandleUpdateAsync(update, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in Telegram poll loop. Retrying in 5s…");
                await Task.Delay(5_000, cancellationToken);
            }
        }

        logger.LogInformation("Telegram polling loop stopped.");
    }

    // ── Update handling ───────────────────────────────────────────────────────

    private async Task HandleUpdateAsync(TelegramUpdate update, CancellationToken cancellationToken)
    {
        var message = update.EffectiveMessage;

        if (message is null || string.IsNullOrWhiteSpace(message.Text))
            return;

        var chatId = message.Chat.Id;
        var sender = message.From?.DisplayName ?? "unknown";

        // ── Whitelist check ───────────────────────────────────────────────────
        if (!_options.AllowedChatIds.Contains(chatId))
        {
            logger.LogWarning(
                "Rejected message from unauthorised chat {ChatId} ({Sender}): {Text}",
                chatId, sender, message.Text[..Math.Min(50, message.Text.Length)]);

            await botClient.SendMessageAsync(
                chatId,
                "🚫 Unauthorised\\. This chat is not in the allowed list\\.",
                parseMode: "MarkdownV2",
                cancellationToken: cancellationToken);
            return;
        }

        logger.LogInformation(
            "Message from {Sender} in chat {ChatId}: {Text}",
            sender, chatId, message.Text[..Math.Min(80, message.Text.Length)]);

        // ── Serialise per-chat ────────────────────────────────────────────────
        var sem = GetChatLock(chatId);
        await sem.WaitAsync(cancellationToken);

        try
        {
            await ProcessMessageAsync(message, cancellationToken);
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task ProcessMessageAsync(TelegramMessage message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var cmd = TelegramCommandRouter.Parse(message.Text!);

        // Send typing indicator to give instant visual feedback
        if (_options.SendTypingIndicator)
            _ = botClient.SendTypingAsync(chatId, cancellationToken);

        var response = await router.DispatchAsync(
            cmd, chatId, cancellationToken: cancellationToken);

        await botClient.SendMessageAsync(
            chatId,
            response,
            parseMode: _options.ParseMode,
            replyToMessageId: message.MessageId,
            cancellationToken: cancellationToken);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private SemaphoreSlim GetChatLock(long chatId)
    {
        lock (_lockMap)
        {
            if (!_chatLocks.TryGetValue(chatId, out var sem))
            {
                sem = new SemaphoreSlim(1, 1);
                _chatLocks[chatId] = sem;
            }
            return sem;
        }
    }
}
