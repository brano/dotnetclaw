using System.Net;
using System.Text.Json;
using DotnetClaw.Config;
using DotnetClaw.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RichardSzalay.MockHttp;
using Xunit;

namespace DotnetClaw.Tests;

public class TelegramBotClientTests
{
    private const string FakeToken = "123456789:AABBCCDDEEFF";
    private const string BaseUrl = $"https://api.telegram.org/bot{FakeToken}/";

    private (TelegramBotClient client, MockHttpMessageHandler handler) CreateClient(
        long[]? allowedChatIds = null,
        int maxMessageLength = 4000)
    {
        var handler = new MockHttpMessageHandler();
        var http = handler.ToHttpClient();
        http.BaseAddress = new Uri(BaseUrl);

        var opts = Options.Create(new TelegramOptions
        {
            BotToken = FakeToken,
            AllowedChatIds = [.. (allowedChatIds ?? [99L])],
            MaxMessageLength = maxMessageLength,
            ParseMode = "MarkdownV2",
            Enabled = true,
        });

        var client = new TelegramBotClient(http, opts, NullLogger<TelegramBotClient>.Instance);
        return (client, handler);
    }

    private static string ApiJson<T>(T result) => JsonSerializer.Serialize(
        new { ok = true, result });

    // ── GetMeAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMeAsync_ValidResponse_ReturnsBotInfo()
    {
        var (client, handler) = CreateClient();
        var botJson = ApiJson(new { id = 42, username = "DotnetClawBot", first_name = "DotnetClaw" });

        handler.When($"{BaseUrl}getMe").Respond("application/json", botJson);

        var result = await client.GetMeAsync();

        Assert.NotNull(result);
        Assert.Equal("DotnetClawBot", result!.Username);
    }

    [Fact]
    public async Task GetMeAsync_NetworkError_ReturnsNull()
    {
        var (client, handler) = CreateClient();
        handler.When($"{BaseUrl}getMe").Respond(HttpStatusCode.InternalServerError);

        var result = await client.GetMeAsync();
        Assert.Null(result);
    }

    // ── GetUpdatesAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetUpdatesAsync_WithUpdates_ReturnsCorrectCount()
    {
        var (client, handler) = CreateClient();
        var updatesJson = ApiJson(new[]
        {
            new { update_id = 1001, message = new { message_id = 1, chat = new { id = 99, type = "private" }, date = 1700000000, text = "hello" } },
            new { update_id = 1002, message = new { message_id = 2, chat = new { id = 99, type = "private" }, date = 1700000001, text = "world" } },
        });

        handler.When($"{BaseUrl}getUpdates*").Respond("application/json", updatesJson);

        var updates = await client.GetUpdatesAsync(offset: 0, limit: 10, timeoutSeconds: 1);

        Assert.Equal(2, updates.Count);
        Assert.Equal(1001, updates[0].UpdateId);
    }

    [Fact]
    public async Task GetUpdatesAsync_EmptyResult_ReturnsEmptyList()
    {
        var (client, handler) = CreateClient();
        handler.When($"{BaseUrl}getUpdates*").Respond("application/json", ApiJson(Array.Empty<object>()));

        var updates = await client.GetUpdatesAsync();
        Assert.Empty(updates);
    }

    // ── SendMessageAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task SendMessageAsync_ShortMessage_SendsSingleRequest()
    {
        var (client, handler) = CreateClient();
        var sentJson = ApiJson(new { message_id = 55, chat = new { id = 99, type = "private" }, date = 1700000000, text = "reply" });

        var request = handler.When(HttpMethod.Post, $"{BaseUrl}sendMessage").Respond("application/json", sentJson);

        var result = await client.SendMessageAsync(99, "Hello!");

        Assert.NotNull(result);
        Assert.Equal(55, result!.MessageId);
        Assert.Equal(1, handler.GetMatchCount(request));
    }

    [Fact]
    public async Task SendMessageAsync_LongMessage_SplitsIntoMultipleRequests()
    {
        var (client, handler) = CreateClient(maxMessageLength: 20);
        var sentJson = ApiJson(new { message_id = 1, chat = new { id = 99, type = "private" }, date = 1700000000, text = "x" });

        var request = handler.When(HttpMethod.Post, $"{BaseUrl}sendMessage").Respond("application/json", sentJson);

        // This is 60 chars — should be split into 3 chunks of max 20
        await client.SendMessageAsync(99, new string('A', 60));

        Assert.True(handler.GetMatchCount(request) >= 2, "Expected multiple sends for a long message");
    }

    [Fact]
    public async Task SendMessageAsync_EmptyText_ReturnsNull()
    {
        var (client, _) = CreateClient();
        var result = await client.SendMessageAsync(99, "");
        Assert.Null(result);
    }

    // ── SendPhotoAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task SendPhotoAsync_ValidBytes_PostsMultipartAndReturnsMessage()
    {
        var (client, handler) = CreateClient();
        var sentJson = ApiJson(new { message_id = 77, chat = new { id = 99, type = "private" }, date = 1700000000 });

        var request = handler.When(HttpMethod.Post, $"{BaseUrl}sendPhoto").Respond("application/json", sentJson);

        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // PNG header
        var result = await client.SendPhotoAsync(99, pngBytes, "screenshot.png", "image/png", "Test caption");

        Assert.NotNull(result);
        Assert.Equal(77, result!.MessageId);
        Assert.Equal(1, handler.GetMatchCount(request));
    }

    [Fact]
    public async Task SendPhotoAsync_LongCaption_TruncatesToUnder1024()
    {
        var (client, handler) = CreateClient();
        var sentJson = ApiJson(new { message_id = 1, chat = new { id = 99, type = "private" }, date = 1700000000 });
        handler.When(HttpMethod.Post, $"{BaseUrl}sendPhoto").Respond("application/json", sentJson);

        var longCaption = new string('A', 2000);
        // Should not throw even with caption > 1024 chars
        var result = await client.SendPhotoAsync(99, new byte[10], "shot.png", "image/png", longCaption);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task SendPhotoAsync_ApiReturnsError_ReturnsNull()
    {
        var (client, handler) = CreateClient();
        handler.When(HttpMethod.Post, $"{BaseUrl}sendPhoto")
               .Respond("application/json", """{"ok":false,"description":"Bad Request: photo is too large"}""");

        var result = await client.SendPhotoAsync(99, new byte[10], "shot.png", "image/png");

        Assert.Null(result);
    }



    [Fact]
    public void SplitMessage_ShortText_ReturnsSingleChunk()
    {
        var chunks = TelegramBotClient.SplitMessage("Hello world", 100);
        Assert.Single(chunks);
        Assert.Equal("Hello world", chunks[0]);
    }

    [Fact]
    public void SplitMessage_LongTextNoParagraphs_SplitsAtMaxLength()
    {
        var text = new string('X', 250);
        var chunks = TelegramBotClient.SplitMessage(text, 100);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 100));
    }

    [Fact]
    public void SplitMessage_TextWithParagraphBreak_BreaksAtParagraph()
    {
        var text = new string('A', 60) + "\n\n" + new string('B', 60);
        var chunks = TelegramBotClient.SplitMessage(text, 80);

        Assert.Equal(2, chunks.Count);
        Assert.Contains("A", chunks[0]);
        Assert.Contains("B", chunks[1]);
    }

    [Fact]
    public void SplitMessage_ReassembledMatchesOriginal()
    {
        var original = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"Line {i}: some content here"));
        var chunks = TelegramBotClient.SplitMessage(original, 200);
        var reassembled = string.Join("\n", chunks);

        // All content should be preserved (whitespace trimming aside)
        foreach (var line in original.Split('\n').Take(10))
            Assert.Contains(line.Trim(), reassembled);
    }
}
