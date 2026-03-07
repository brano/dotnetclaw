using System.Text;
using DotnetClaw.Browser;
using DotnetClaw.Config;
using DotnetClaw.Plugins;
using DotnetClaw.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace DotnetClaw.Tests;

// ============================================================================
//  BrowserPlugin Tests
//  Uses a MockBrowserSession to avoid launching a real browser.
// ============================================================================

public class BrowserPluginTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static BrowserPlugin CreatePlugin(
        MockBrowserSession session,
        ITelegramBotClient? telegram = null,
        BrowserOptions? browserOpts = null,
        TelegramOptions? telegramOpts = null)
    {
        var mgr = new Mock<BrowserSessionManager>(
            Options.Create(browserOpts ?? DefaultBrowserOptions()),
            NullLogger<BrowserSessionManager>.Instance);

        mgr.Setup(m => m.GetSessionAsync(It.IsAny<CancellationToken>()))
           .ReturnsAsync(session);

        return new BrowserPlugin(
            mgr.Object,
            telegram ?? new Mock<ITelegramBotClient>().Object,
            Options.Create(browserOpts ?? DefaultBrowserOptions()),
            Options.Create(telegramOpts ?? DefaultTelegramOptions()),
            NullLogger<BrowserPlugin>.Instance);
    }

    private static BrowserOptions DefaultBrowserOptions() => new()
    {
        BrowserType = "chromium",
        Headless = true,
        DefaultTimeoutMs = 5000,
        ScreenshotDirectory = Path.Combine(Path.GetTempPath(), "dotnetclaw_test_screenshots"),
        ScreenshotFormat = "png",
        PersistBrowserSession = false,
    };

    private static TelegramOptions DefaultTelegramOptions() => new()
    {
        Enabled = true,
        BotToken = "fake-token",
        AllowedChatIds = [99L],
    };

    // ── browser_navigate ──────────────────────────────────────────────────────

    [Fact]
    public async Task Navigate_Success_ReturnsOkResult()
    {
        var session = new MockBrowserSession();
        session.SetupNavigate("https://example.com", new BrowserNavigateResult
        {
            Url = "https://example.com",
            Title = "Example Domain",
            HttpStatus = 200,
            LoadTime = TimeSpan.FromMilliseconds(350),
        });

        var plugin = CreatePlugin(session);
        var result = await plugin.NavigateAsync("https://example.com");

        Assert.StartsWith("[OK]", result);
        Assert.Contains("Example Domain", result);
        Assert.Contains("200", result);
    }

    [Fact]
    public async Task Navigate_Timeout_ReturnsTimeoutMessage()
    {
        var session = new MockBrowserSession();
        session.SetupNavigateThrows(new BrowserTimeoutException("Timed out after 5000ms."));

        var plugin = CreatePlugin(session);
        var result = await plugin.NavigateAsync("https://slow.example.com");

        Assert.StartsWith("[TIMEOUT]", result);
    }

    [Fact]
    public async Task Navigate_UnexpectedError_ReturnsErrorMessage()
    {
        var session = new MockBrowserSession();
        session.SetupNavigateThrows(new InvalidOperationException("Network unreachable"));

        var plugin = CreatePlugin(session);
        var result = await plugin.NavigateAsync("https://bad.example.com");

        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("Network unreachable", result);
    }

    // ── browser_screenshot ────────────────────────────────────────────────────

    [Fact]
    public async Task Screenshot_ReturnsSavedFilePath()
    {
        var session = new MockBrowserSession();
        var fakeBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG magic bytes
        session.SetupScreenshot(fakeBytes);

        var plugin = CreatePlugin(session);
        var result = await plugin.ScreenshotAsync();

        Assert.StartsWith("[OK]", result);
        Assert.Contains("screenshot", result.ToLower());
        Assert.Contains("4", result); // byte count
    }

    [Fact]
    public async Task Screenshot_ElementNotFound_ReturnsNotFound()
    {
        var session = new MockBrowserSession();
        session.SetupScreenshotThrows(new BrowserElementNotFoundException("Element not found: #missing"));

        var plugin = CreatePlugin(session);
        var result = await plugin.ScreenshotAsync(cssSelector: "#missing");

        Assert.StartsWith("[NOT FOUND]", result);
    }

    // ── browser_screenshot_and_send ───────────────────────────────────────────

    [Fact]
    public async Task ScreenshotAndSend_SendsPhotoToTelegram()
    {
        var fakeBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var session = new MockBrowserSession();
        session.SetupScreenshot(fakeBytes);
        session.SetCurrentUrl("https://example.com");
        session.SetCurrentTitle("Example");

        var sentMessage = new TelegramMessage
        {
            MessageId = 42,
            Chat = new TelegramChat { Id = 99, Type = "private" },
            Date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        var telegramMock = new Mock<ITelegramBotClient>();
        telegramMock.Setup(t => t.SendPhotoAsync(
                99L, fakeBytes, It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sentMessage);

        var plugin = CreatePlugin(session, telegramMock.Object);
        var result = await plugin.ScreenshotAndSendAsync(chatId: 99);

        Assert.StartsWith("[OK]", result);
        Assert.Contains("42", result); // message_id
        telegramMock.Verify(t => t.SendPhotoAsync(
            99L, fakeBytes, It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScreenshotAndSend_TelegramDisabled_ReturnsSkip()
    {
        var session = new MockBrowserSession();
        session.SetupScreenshot(new byte[10]);

        var disabledOpts = new TelegramOptions { Enabled = false };
        var plugin = CreatePlugin(session, telegramOpts: disabledOpts);
        var result = await plugin.ScreenshotAndSendAsync();

        Assert.StartsWith("[SKIP]", result);
    }

    [Fact]
    public async Task ScreenshotAndSend_UnauthorisedChat_ReturnsBlocked()
    {
        var session = new MockBrowserSession();
        session.SetupScreenshot(new byte[10]);

        var opts = DefaultTelegramOptions(); // AllowedChatIds = [99]
        var plugin = CreatePlugin(session, telegramOpts: opts);
        var result = await plugin.ScreenshotAndSendAsync(chatId: 999); // not in whitelist

        Assert.StartsWith("[BLOCKED]", result);
    }

    // ── browser_get_text ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetText_ReturnsPageText()
    {
        var session = new MockBrowserSession();
        session.SetupGetText("Hello, World! This is the page content.");

        var plugin = CreatePlugin(session);
        var result = await plugin.GetTextAsync();

        Assert.Equal("Hello, World! This is the page content.", result);
    }

    [Fact]
    public async Task GetText_VeryLongText_Truncates()
    {
        var session = new MockBrowserSession();
        session.SetupGetText(new string('X', 10_000));

        var plugin = CreatePlugin(session);
        var result = await plugin.GetTextAsync();

        Assert.True(result.Length < 10_000);
        Assert.Contains("truncated", result);
    }

    [Fact]
    public async Task GetText_ElementNotFound_ReturnsNotFound()
    {
        var session = new MockBrowserSession();
        session.SetupGetTextThrows(new BrowserElementNotFoundException("Element not found: #ghost"));

        var plugin = CreatePlugin(session);
        var result = await plugin.GetTextAsync(cssSelector: "#ghost");

        Assert.StartsWith("[NOT FOUND]", result);
    }

    // ── browser_fill ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Fill_Success_ReturnsOk()
    {
        var session = new MockBrowserSession();
        var plugin = CreatePlugin(session);
        var result = await plugin.FillAsync("#email", "test@example.com");

        Assert.StartsWith("[OK]", result);
        Assert.Contains("#email", result);
        Assert.Equal(("#email", "test@example.com"), session.LastFill);
    }

    [Fact]
    public async Task Fill_ElementNotFound_ReturnsNotFound()
    {
        var session = new MockBrowserSession();
        session.SetupFillThrows(new BrowserElementNotFoundException("Element not found: #ghost"));

        var plugin = CreatePlugin(session);
        var result = await plugin.FillAsync("#ghost", "value");

        Assert.StartsWith("[NOT FOUND]", result);
    }

    // ── browser_click ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Click_Success_ReturnsOk()
    {
        var session = new MockBrowserSession();
        var plugin = CreatePlugin(session);
        var result = await plugin.ClickAsync("button[type='submit']");

        Assert.StartsWith("[OK]", result);
        Assert.Equal("button[type='submit']", session.LastClick);
    }

    [Fact]
    public async Task Click_ElementNotFound_ReturnsNotFound()
    {
        var session = new MockBrowserSession();
        session.SetupClickThrows(new BrowserElementNotFoundException("Button not found"));

        var plugin = CreatePlugin(session);
        var result = await plugin.ClickAsync("#missing-btn");

        Assert.StartsWith("[NOT FOUND]", result);
    }

    // ── browser_submit_form ───────────────────────────────────────────────────

    [Fact]
    public async Task SubmitForm_FillsAllFieldsAndClicks()
    {
        var session = new MockBrowserSession();
        var plugin = CreatePlugin(session);

        var fields = "#username=alice\n#password=secret123";
        var result = await plugin.SubmitFormAsync(fields, "button[type='submit']");

        Assert.StartsWith("[OK]", result);
        Assert.Contains("#username", result);
        Assert.Contains("#password", result);
        Assert.Equal(2, session.FillHistory.Count);
        Assert.Equal(("username", "alice"),   (session.FillHistory[0].selector.TrimStart('#'), session.FillHistory[0].value));
        Assert.Equal(("password", "secret123"), (session.FillHistory[1].selector.TrimStart('#'), session.FillHistory[1].value));
        Assert.Equal("button[type='submit']", session.LastClick);
    }

    [Fact]
    public async Task SubmitForm_MalformedLine_SkipsAndContinues()
    {
        var session = new MockBrowserSession();
        var plugin = CreatePlugin(session);

        // Line without '=' should be skipped, valid lines should still be filled
        var fields = "BADLINE\n#email=user@test.com";
        var result = await plugin.SubmitFormAsync(fields, "#submit");

        Assert.StartsWith("[OK]", result);
        Assert.Single(session.FillHistory);
    }

    // ── browser_evaluate ──────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_ReturnsScriptResult()
    {
        var session = new MockBrowserSession();
        session.SetupEvaluate("42");

        var plugin = CreatePlugin(session);
        var result = await plugin.EvaluateAsync("return 6 * 7");

        Assert.Equal("42", result);
    }

    [Fact]
    public async Task Evaluate_NullResult_ReturnsNullString()
    {
        var session = new MockBrowserSession();
        session.SetupEvaluate(null);

        var plugin = CreatePlugin(session);
        var result = await plugin.EvaluateAsync("return null");

        Assert.Equal("(null)", result);
    }

    [Fact]
    public async Task Evaluate_ScriptError_ReturnsErrorMessage()
    {
        var session = new MockBrowserSession();
        session.SetupEvaluateThrows(new InvalidOperationException("Syntax error"));

        var plugin = CreatePlugin(session);
        var result = await plugin.EvaluateAsync("return !!!invalid");

        Assert.StartsWith("[ERROR]", result);
    }
}

// ============================================================================
//  MockBrowserSession — controllable fake for unit tests
// ============================================================================

internal sealed class MockBrowserSession : IBrowserSession
{
    // ── State ─────────────────────────────────────────────────────────────────

    private string _currentUrl   = "about:blank";
    private string _currentTitle = "Blank";

    public string CurrentUrl   => _currentUrl;
    public string CurrentTitle => _currentTitle;

    public void SetCurrentUrl(string url)     => _currentUrl   = url;
    public void SetCurrentTitle(string title) => _currentTitle = title;

    // ── Navigate setup ─────────────────────────────────────────────────────────

    private BrowserNavigateResult? _navigateResult;
    private Exception? _navigateException;

    public void SetupNavigate(string url, BrowserNavigateResult result)
    {
        _currentUrl = url;
        _navigateResult = result;
    }

    public void SetupNavigateThrows(Exception ex) => _navigateException = ex;

    public Task<BrowserNavigateResult> NavigateAsync(string url, int? timeoutMs, CancellationToken ct)
    {
        if (_navigateException is not null) throw _navigateException;
        _currentUrl = url;
        return Task.FromResult(_navigateResult ?? new BrowserNavigateResult
        {
            Url = url, Title = "Test Page", HttpStatus = 200, LoadTime = TimeSpan.FromMilliseconds(100)
        });
    }

    // ── Screenshot setup ───────────────────────────────────────────────────────

    private byte[]? _screenshotBytes;
    private Exception? _screenshotException;

    public void SetupScreenshot(byte[] bytes) => _screenshotBytes = bytes;
    public void SetupScreenshotThrows(Exception ex) => _screenshotException = ex;

    public Task<byte[]> ScreenshotAsync(string? cssSelector, bool fullPage, CancellationToken ct)
    {
        if (_screenshotException is not null) throw _screenshotException;
        return Task.FromResult(_screenshotBytes ?? [0x89, 0x50, 0x4E, 0x47]);
    }

    // ── GetText setup ──────────────────────────────────────────────────────────

    private string _textResult = "Page text";
    private Exception? _textException;

    public void SetupGetText(string text)        => _textResult    = text;
    public void SetupGetTextThrows(Exception ex) => _textException = ex;

    public Task<string> GetTextAsync(string? cssSelector, CancellationToken ct)
    {
        if (_textException is not null) throw _textException;
        return Task.FromResult(_textResult);
    }

    // ── Fill setup ─────────────────────────────────────────────────────────────

    public (string selector, string value) LastFill { get; private set; }
    public List<(string selector, string value)> FillHistory { get; } = [];
    private Exception? _fillException;

    public void SetupFillThrows(Exception ex) => _fillException = ex;

    public Task FillAsync(string cssSelector, string value, CancellationToken ct)
    {
        if (_fillException is not null) throw _fillException;
        LastFill = (cssSelector, value);
        FillHistory.Add((cssSelector, value));
        return Task.CompletedTask;
    }

    // ── Click setup ────────────────────────────────────────────────────────────

    public string? LastClick { get; private set; }
    private Exception? _clickException;

    public void SetupClickThrows(Exception ex) => _clickException = ex;

    public Task ClickAsync(string cssSelector, CancellationToken ct)
    {
        if (_clickException is not null) throw _clickException;
        LastClick = cssSelector;
        return Task.CompletedTask;
    }

    // ── SelectOption / WaitForSelector (passthrough stubs) ────────────────────

    public Task SelectOptionAsync(string cssSelector, string value, CancellationToken ct)
        => Task.CompletedTask;

    public Task WaitForSelectorAsync(string cssSelector, int? timeoutMs, CancellationToken ct)
        => Task.CompletedTask;

    // ── Evaluate setup ─────────────────────────────────────────────────────────

    private string? _evaluateResult = "result";
    private Exception? _evaluateException;

    public void SetupEvaluate(string? result)    => _evaluateResult    = result;
    public void SetupEvaluateThrows(Exception ex) => _evaluateException = ex;

    public Task<string?> EvaluateAsync(string script, CancellationToken ct)
    {
        if (_evaluateException is not null) throw _evaluateException;
        return Task.FromResult(_evaluateResult);
    }

    // ── IAsyncDisposable ───────────────────────────────────────────────────────

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
