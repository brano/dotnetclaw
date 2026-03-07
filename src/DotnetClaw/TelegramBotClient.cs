using System.Net.Http.Json;
using System.Text.Json;
using DotnetClaw.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetClaw.Telegram;

// ============================================================================
//  Telegram Bot API Client — minimal, no SDK, pure HttpClient
// ============================================================================

/// <summary>
/// Minimal Telegram Bot API client interface.
/// Only the methods DotnetClaw needs to send and receive messages.
/// </summary>
public interface ITelegramBotClient
{
    /// <summary>Verify connectivity and return bot identity.</summary>
    Task<TelegramBotInfo?> GetMeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Long-poll for new updates. Pass <paramref name="offset"/> = last_update_id + 1
    /// to acknowledge processed updates and avoid re-delivering them.
    /// </summary>
    Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        long offset = 0,
        int limit = 10,
        int timeoutSeconds = 30,
        CancellationToken cancellationToken = default);

    /// <summary>Send a text message to a chat. Splits automatically if over 4096 chars.</summary>
    Task<TelegramMessage?> SendMessageAsync(
        long chatId,
        string text,
        string? parseMode = null,
        long? replyToMessageId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Send a "typing…" chat action so the user sees feedback.</summary>
    Task SendTypingAsync(long chatId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upload and send a photo (PNG or JPEG bytes) to a Telegram chat.
    /// Uses multipart/form-data — no file size restriction beyond Telegram's 10 MB photo limit.
    /// </summary>
    Task<TelegramMessage?> SendPhotoAsync(
        long chatId,
        byte[] photoBytes,
        string fileName,
        string mimeType,
        string? caption = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Raw <see cref="HttpClient"/>-based Telegram Bot API client.
/// Base URL: <c>https://api.telegram.org/bot{token}/</c>
/// </summary>
public sealed class TelegramBotClient : ITelegramBotClient
{
    private readonly HttpClient _http;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramBotClient> _logger;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public TelegramBotClient(
        HttpClient http,
        IOptions<TelegramOptions> options,
        ILogger<TelegramBotClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;

        // Set base address once using the bot token
        var token = _options.EffectiveBotToken;
        if (!string.IsNullOrWhiteSpace(token))
            _http.BaseAddress = new Uri($"https://api.telegram.org/bot{token}/");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<TelegramBotInfo?> GetMeAsync(CancellationToken cancellationToken = default)
    {
        var response = await GetAsync<TelegramBotInfo>("getMe", cancellationToken);
        if (response?.Ok == true)
            _logger.LogInformation("Connected as Telegram bot @{Username}", response.Result?.Username);
        else
            _logger.LogError("getMe failed: {Desc}", response?.Description);
        return response?.Result;
    }

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        long offset = 0,
        int limit = 10,
        int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        var url = $"getUpdates?offset={offset}&limit={limit}&timeout={timeoutSeconds}";

        // Long-poll timeout must exceed the HTTP client timeout — give a buffer
        using var httpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        httpCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + 10));

        var response = await GetAsync<TelegramUpdate[]>(url, httpCts.Token);
        if (response?.Ok != true)
        {
            _logger.LogWarning("getUpdates returned not-ok: {Desc}", response?.Description);
            return [];
        }

        return response.Result ?? [];
    }

    public async Task<TelegramMessage?> SendMessageAsync(
        long chatId,
        string text,
        string? parseMode = null,
        long? replyToMessageId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Split long messages at paragraph/line boundaries
        var chunks = SplitMessage(text, _options.MaxMessageLength);
        TelegramMessage? lastSent = null;

        foreach (var (chunk, idx) in chunks.Select((c, i) => (c, i)))
        {
            var payload = new SendMessageRequest
            {
                ChatId = chatId,
                Text = chunk,
                ParseMode = string.IsNullOrWhiteSpace(parseMode) ? null : parseMode,
                ReplyToMessageId = idx == 0 ? replyToMessageId : null,
            };

            var response = await PostAsync<SendMessageRequest, TelegramMessage>(
                "sendMessage", payload, cancellationToken);

            if (response?.Ok != true)
            {
                _logger.LogWarning(
                    "sendMessage failed (chunk {I}/{N}): {Desc}",
                    idx + 1, chunks.Count, response?.Description);

                // If MarkdownV2 fails (bad escaping), retry as plain text
                if (parseMode is "MarkdownV2")
                {
                    _logger.LogInformation("Retrying chunk {I} as plain text.", idx + 1);
                    var plain = new SendMessageRequest { ChatId = chatId, Text = chunk };
                    var retry = await PostAsync<SendMessageRequest, TelegramMessage>(
                        "sendMessage", plain, cancellationToken);
                    lastSent = retry?.Result;
                }
            }
            else
            {
                lastSent = response.Result;
            }
        }

        return lastSent;
    }

    public async Task SendTypingAsync(long chatId, CancellationToken cancellationToken = default)
    {
        var payload = new SendChatActionRequest { ChatId = chatId };
        await PostAsync<SendChatActionRequest, bool>("sendChatAction", payload, cancellationToken);
    }

    public async Task<TelegramMessage?> SendPhotoAsync(
        long chatId,
        byte[] photoBytes,
        string fileName,
        string mimeType,
        string? caption = null,
        CancellationToken cancellationToken = default)
    {
        // Telegram's sendPhoto requires multipart/form-data when uploading raw bytes
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(chatId.ToString()), "chat_id");

        var imageContent = new ByteArrayContent(photoBytes);
        imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
        form.Add(imageContent, "photo", fileName);

        if (!string.IsNullOrWhiteSpace(caption))
        {
            // Caption max 1024 chars; truncate gracefully
            var safeCaption = caption.Length > 1024 ? caption[..1021] + "…" : caption;
            form.Add(new StringContent(safeCaption), "caption");
        }

        try
        {
            var httpResponse = await _http.PostAsync("sendPhoto", form, cancellationToken);
            httpResponse.EnsureSuccessStatusCode();
            var apiResponse = await httpResponse.Content
                .ReadFromJsonAsync<TelegramApiResponse<TelegramMessage>>(_json, cancellationToken);

            if (apiResponse?.Ok != true)
            {
                _logger.LogWarning("sendPhoto failed: {Desc}", apiResponse?.Description);
                return null;
            }

            _logger.LogInformation(
                "Photo sent to chat {ChatId} ({Bytes} bytes, message_id={MsgId})",
                chatId, photoBytes.Length, apiResponse.Result?.MessageId);

            return apiResponse.Result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "sendPhoto HTTP request failed for chat {ChatId}", chatId);
            return null;
        }
    }

    // ── Private HTTP helpers ──────────────────────────────────────────────────

    private async Task<TelegramApiResponse<T>?> GetAsync<T>(
        string endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _http.GetAsync(endpoint, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<TelegramApiResponse<T>>(
                _json, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "HTTP GET {Endpoint} failed", endpoint);
            return null;
        }
    }

    private async Task<TelegramApiResponse<TResponse>?> PostAsync<TRequest, TResponse>(
        string endpoint,
        TRequest payload,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(endpoint, payload, _json, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<TelegramApiResponse<TResponse>>(
                _json, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "HTTP POST {Endpoint} failed", endpoint);
            return null;
        }
    }

    // ── Message splitting ─────────────────────────────────────────────────────

    /// <summary>
    /// Split a message into chunks of at most <paramref name="maxLength"/> characters.
    /// Tries to break on double-newlines (paragraphs) first, then single newlines, then
    /// hard-cuts at <paramref name="maxLength"/> as a last resort.
    /// </summary>
    internal static List<string> SplitMessage(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return [text];

        var chunks = new List<string>();
        var remaining = text.AsSpan();

        while (remaining.Length > maxLength)
        {
            var slice = remaining[..maxLength];

            // Try paragraph break
            var breakAt = slice.LastIndexOf("\n\n");
            if (breakAt < maxLength / 2) breakAt = -1; // too early — not worth it

            // Fall back to single newline
            if (breakAt < 0)
                breakAt = slice.LastIndexOf('\n');

            // Hard cut
            if (breakAt < 0)
                breakAt = maxLength - 1;

            chunks.Add(remaining[..(breakAt + 1)].ToString().TrimEnd());
            remaining = remaining[(breakAt + 1)..].TrimStart();
        }

        if (!remaining.IsEmpty)
            chunks.Add(remaining.ToString());

        return chunks;
    }
}
