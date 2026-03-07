using System.ComponentModel;
using DotnetClaw.Browser;
using DotnetClaw.Config;
using DotnetClaw.Telegram;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace DotnetClaw.Plugins;

/// <summary>
/// Browser skill — gives DotnetClaw a full headless browser via Playwright.
///
/// Capabilities:
///   • Navigate to any URL and inspect the result
///   • Take screenshots (whole page, visible viewport, or a specific element)
///   • Send screenshots directly to Telegram as photo messages
///   • Read text content from the page or from specific elements
///   • Fill in form fields by CSS selector
///   • Click buttons and links
///   • Submit entire forms with a single call
///   • Run arbitrary JavaScript on the page
///
/// Prerequisites (one-time setup):
///   dotnet tool install --global Microsoft.Playwright.CLI
///   playwright install chromium
/// </summary>
public sealed class BrowserPlugin(
    BrowserSessionManager sessionManager,
    ITelegramBotClient telegramClient,
    IOptions<BrowserOptions> browserOptions,
    IOptions<TelegramOptions> telegramOptions,
    ILogger<BrowserPlugin> logger)
{
    private readonly BrowserOptions _browserOptions = browserOptions.Value;
    private readonly TelegramOptions _telegramOptions = telegramOptions.Value;

    // =========================================================================
    // Navigation
    // =========================================================================

    [KernelFunction("browser_navigate")]
    [Description(
        "Navigate the browser to a URL and wait for the page to finish loading. " +
        "Returns the page title, final URL (after any redirects), HTTP status code, and load time. " +
        "Use this before taking screenshots or interacting with a page.")]
    public async Task<string> NavigateAsync(
        [Description("Full URL to navigate to, including scheme. Example: 'https://example.com'")]
        string url,

        [Description("Optional: navigation timeout in milliseconds. Defaults to BrowserOptions.DefaultTimeoutMs.")]
        int? timeoutMs = null,

        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = await sessionManager.GetSessionAsync(cancellationToken);
            var result = await session.NavigateAsync(url, timeoutMs, cancellationToken);
            return $"[OK] {result}";
        }
        catch (BrowserTimeoutException ex)
        {
            logger.LogWarning("Navigation timeout: {Url}", url);
            return $"[TIMEOUT] {ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Navigation failed: {Url}", url);
            return $"[ERROR] Navigation failed: {ex.Message}";
        }
    }

    // =========================================================================
    // Screenshots
    // =========================================================================

    [KernelFunction("browser_screenshot")]
    [Description(
        "Take a screenshot of the current browser page and save it to the screenshots directory. " +
        "Optionally target a specific element by CSS selector, or capture the full scrollable page. " +
        "Returns the file path of the saved screenshot.")]
    public async Task<string> ScreenshotAsync(
        [Description(
            "Optional CSS selector to screenshot a specific element. " +
            "Example: '#main-content' or '.hero-image'. " +
            "Leave empty to screenshot the visible viewport.")]
        string? cssSelector = null,

        [Description("When true, captures the full scrollable page height, not just the visible area.")]
        bool fullPage = false,

        [Description("Optional filename (without extension). Defaults to a timestamp-based name.")]
        string? fileName = null,

        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = await sessionManager.GetSessionAsync(cancellationToken);
            var bytes = await session.ScreenshotAsync(cssSelector, fullPage, cancellationToken);
            var path = await SaveScreenshotAsync(bytes, fileName);

            logger.LogInformation("Screenshot saved: {Path} ({Bytes} bytes)", path, bytes.Length);
            return $"[OK] Screenshot saved: {path} ({bytes.Length:N0} bytes)";
        }
        catch (BrowserElementNotFoundException ex)
        {
            return $"[NOT FOUND] {ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Screenshot failed");
            return $"[ERROR] Screenshot failed: {ex.Message}";
        }
    }

    [KernelFunction("browser_screenshot_and_send")]
    [Description(
        "Take a screenshot of the current browser page and send it directly as a Telegram photo message. " +
        "Combines browser_screenshot + Telegram photo delivery in one step. " +
        "Returns a confirmation with the Telegram message ID.")]
    public async Task<string> ScreenshotAndSendAsync(
        [Description("Optional CSS selector to screenshot a specific element. Leave empty for viewport.")]
        string? cssSelector = null,

        [Description("When true, captures the full scrollable page height.")]
        bool fullPage = false,

        [Description("Optional caption to include with the Telegram photo. Supports plain text.")]
        string? caption = null,

        [Description("Optional target Telegram chat ID. Defaults to the first AllowedChatId.")]
        long? chatId = null,

        CancellationToken cancellationToken = default)
    {
        if (!_telegramOptions.Enabled || !_telegramOptions.IsConfigured)
            return "[SKIP] Telegram is not configured. Use browser_screenshot to save to disk instead.";

        try
        {
            await using var session = await sessionManager.GetSessionAsync(cancellationToken);
            var bytes = await session.ScreenshotAsync(cssSelector, fullPage, cancellationToken);

            // Also save locally for audit trail
            var localPath = await SaveScreenshotAsync(bytes, null);

            var targetChat = chatId ?? _telegramOptions.AllowedChatIds.FirstOrDefault();
            if (targetChat == 0)
                return "[ERROR] No Telegram chat ID configured.";

            if (!_telegramOptions.AllowedChatIds.Contains(targetChat))
                return $"[BLOCKED] Chat {targetChat} is not in AllowedChatIds.";

            var ext = _browserOptions.ScreenshotFormat.ToLowerInvariant();
            var mimeType = ext is "jpg" or "jpeg" ? "image/jpeg" : "image/png";
            var photoFileName = $"screenshot.{ext}";

            var autoCaption = caption ?? $"📸 {session.CurrentTitle}\n{session.CurrentUrl}";

            var sent = await telegramClient.SendPhotoAsync(
                targetChat, bytes, photoFileName, mimeType, autoCaption, cancellationToken);

            return sent is not null
                ? $"[OK] Screenshot sent to Telegram chat {targetChat} (message_id={sent.MessageId}). Local copy: {localPath}"
                : $"[ERROR] Failed to send screenshot to Telegram. Local copy saved: {localPath}";
        }
        catch (BrowserElementNotFoundException ex)
        {
            return $"[NOT FOUND] {ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "browser_screenshot_and_send failed");
            return $"[ERROR] {ex.Message}";
        }
    }

    // =========================================================================
    // Page content
    // =========================================================================

    [KernelFunction("browser_get_text")]
    [Description(
        "Extract the visible text content from the current page or a specific element. " +
        "Useful for reading page content, error messages, or form labels without a full screenshot.")]
    public async Task<string> GetTextAsync(
        [Description(
            "Optional CSS selector to read text from a specific element. " +
            "Example: 'h1', '#error-message', '.result-list'. " +
            "Leave empty to read the full page body text.")]
        string? cssSelector = null,

        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = await sessionManager.GetSessionAsync(cancellationToken);
            var text = await session.GetTextAsync(cssSelector, cancellationToken);

            // Truncate extremely long pages to avoid flooding the agent context
            const int maxChars = 8000;
            if (text.Length > maxChars)
                return text[..maxChars] + $"\n\n[... {text.Length - maxChars:N0} more characters truncated]";

            return text;
        }
        catch (BrowserElementNotFoundException ex)
        {
            return $"[NOT FOUND] {ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "browser_get_text failed");
            return $"[ERROR] {ex.Message}";
        }
    }

    // =========================================================================
    // Form interaction
    // =========================================================================

    [KernelFunction("browser_fill")]
    [Description(
        "Type a value into a form field identified by a CSS selector. " +
        "Clears any existing value before typing. " +
        "Works with <input>, <textarea>, and contenteditable elements.")]
    public async Task<string> FillAsync(
        [Description(
            "CSS selector of the input element. " +
            "Examples: '#username', 'input[name=\"email\"]', 'textarea.message-body'")]
        string cssSelector,

        [Description("The value to type into the field.")]
        string value,

        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = await sessionManager.GetSessionAsync(cancellationToken);
            await session.FillAsync(cssSelector, value, cancellationToken);
            return $"[OK] Filled '{cssSelector}' with value ({value.Length} chars)";
        }
        catch (BrowserElementNotFoundException ex)
        {
            return $"[NOT FOUND] {ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "browser_fill failed for selector: {Sel}", cssSelector);
            return $"[ERROR] Fill failed: {ex.Message}";
        }
    }

    [KernelFunction("browser_click")]
    [Description(
        "Click an element on the page identified by a CSS selector. " +
        "Waits for the element to be visible and stable before clicking. " +
        "Use this for buttons, links, checkboxes, radio buttons, or any clickable element.")]
    public async Task<string> ClickAsync(
        [Description(
            "CSS selector of the element to click. " +
            "Examples: 'button[type=\"submit\"]', '#login-btn', 'a.nav-link:first-child'")]
        string cssSelector,

        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = await sessionManager.GetSessionAsync(cancellationToken);
            await session.ClickAsync(cssSelector, cancellationToken);
            return $"[OK] Clicked '{cssSelector}'";
        }
        catch (BrowserElementNotFoundException ex)
        {
            return $"[NOT FOUND] {ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "browser_click failed for selector: {Sel}", cssSelector);
            return $"[ERROR] Click failed: {ex.Message}";
        }
    }

    [KernelFunction("browser_submit_form")]
    [Description(
        "Fill multiple form fields and then click a submit button — all in one step. " +
        "Fields is a newline-separated list of 'cssSelector=value' pairs. " +
        "After filling all fields, the submit button is clicked. " +
        "Returns a summary of what was filled and whether submission succeeded.")]
    public async Task<string> SubmitFormAsync(
        [Description(
            "Newline-separated list of field assignments in the format 'cssSelector=value'. " +
            "Example:\n#username=alice\n#password=secret123\ninput[name=remember]=true")]
        string fields,

        [Description(
            "CSS selector of the submit button or form submission element. " +
            "Example: 'button[type=\"submit\"]' or '#submit-btn' or 'input[type=\"submit\"]'")]
        string submitSelector,

        [Description("Optional: wait for this CSS selector to appear after submission (confirms success).")]
        string? successSelector = null,

        CancellationToken cancellationToken = default)
    {
        var filled = new List<string>();
        var errors = new List<string>();

        try
        {
            await using var session = await sessionManager.GetSessionAsync(cancellationToken);

            // Fill each field
            foreach (var line in fields.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eqIdx = line.IndexOf('=');
                if (eqIdx <= 0)
                {
                    errors.Add($"Skipped malformed line (no '='): {line}");
                    continue;
                }

                var selector = line[..eqIdx].Trim();
                var value = line[(eqIdx + 1)..].Trim();

                try
                {
                    await session.FillAsync(selector, value, cancellationToken);
                    filled.Add($"  ✓ {selector}");
                }
                catch (Exception ex)
                {
                    errors.Add($"  ✗ {selector}: {ex.Message}");
                }
            }

            // Click submit
            await session.ClickAsync(submitSelector, cancellationToken);

            // Optionally wait for success indicator
            if (!string.IsNullOrWhiteSpace(successSelector))
            {
                await session.WaitForSelectorAsync(successSelector, cancellationToken: cancellationToken);
                filled.Add($"  ✓ Success indicator '{successSelector}' appeared");
            }

            var summary = new System.Text.StringBuilder();
            summary.AppendLine($"[OK] Form submitted via '{submitSelector}'");
            if (filled.Count > 0)   { summary.AppendLine("Filled:"); filled.ForEach(f => summary.AppendLine(f)); }
            if (errors.Count > 0)   { summary.AppendLine("Errors:"); errors.ForEach(e => summary.AppendLine(e)); }
            return summary.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "browser_submit_form failed");
            return $"[ERROR] Form submission failed: {ex.Message}\n" +
                   (filled.Count > 0 ? $"Fields filled before error:\n{string.Join('\n', filled)}" : "");
        }
    }

    // =========================================================================
    // JavaScript
    // =========================================================================

    [KernelFunction("browser_evaluate")]
    [Description(
        "Execute JavaScript in the context of the current page and return the result. " +
        "The script runs synchronously on the page — use 'return' to return a value. " +
        "Example: 'return document.title' or 'return document.querySelectorAll(\"a\").length'")]
    public async Task<string> EvaluateAsync(
        [Description("JavaScript expression to evaluate. Must use 'return' to return a value.")]
        string script,

        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = await sessionManager.GetSessionAsync(cancellationToken);
            var result = await session.EvaluateAsync(script, cancellationToken);
            return result is null ? "(null)" : result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "browser_evaluate failed");
            return $"[ERROR] JavaScript evaluation failed: {ex.Message}";
        }
    }

    // =========================================================================
    // Private helpers
    // =========================================================================

    private async Task<string> SaveScreenshotAsync(byte[] bytes, string? fileName)
    {
        var dir = Path.IsPathRooted(_browserOptions.ScreenshotDirectory)
            ? _browserOptions.ScreenshotDirectory
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _browserOptions.ScreenshotDirectory));

        Directory.CreateDirectory(dir);

        var ext = _browserOptions.ScreenshotFormat.ToLowerInvariant() is "jpg" ? "jpg"
                : _browserOptions.ScreenshotFormat.ToLowerInvariant() is "jpeg" ? "jpg"
                : "png";

        var name = string.IsNullOrWhiteSpace(fileName)
            ? $"screenshot_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}.{ext}"
            : $"{fileName}.{ext}";

        var fullPath = Path.Combine(dir, name);
        await File.WriteAllBytesAsync(fullPath, bytes);
        return fullPath;
    }
}
