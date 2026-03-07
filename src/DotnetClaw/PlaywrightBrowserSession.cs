using DotnetClaw.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace DotnetClaw.Browser;

/// <summary>
/// Production <see cref="IBrowserSession"/> backed by a Playwright <see cref="IPage"/>.
/// One instance wraps one browser tab.  Disposed by <see cref="BrowserSessionManager"/>.
/// </summary>
public sealed class PlaywrightBrowserSession : IBrowserSession
{
    private readonly IPage _page;
    private readonly BrowserOptions _options;
    private readonly ILogger _logger;

    public PlaywrightBrowserSession(IPage page, BrowserOptions options, ILogger logger)
    {
        _page = page;
        _options = options;
        _logger = logger;

        // Apply configured default timeout
        _page.SetDefaultTimeout(options.DefaultTimeoutMs);
        _page.SetDefaultNavigationTimeout(options.DefaultTimeoutMs);
    }

    // ── IBrowserSession ───────────────────────────────────────────────────────

    public string CurrentUrl   => _page.Url;
    public string CurrentTitle => _page.Title().GetAwaiter().GetResult();

    public async Task<BrowserNavigateResult> NavigateAsync(
        string url,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Browser navigating to: {Url}", url);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var timeout = timeoutMs ?? _options.DefaultTimeoutMs;
        IResponse? response = null;

        try
        {
            response = await _page.GotoAsync(url, new PageGotoOptions
            {
                Timeout = timeout,
                WaitUntil = WaitUntilState.NetworkIdle,
            });
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning("Navigation timed out after {Ms}ms: {Url}", timeout, url);
            throw new BrowserTimeoutException($"Navigation to '{url}' timed out after {timeout}ms.", ex);
        }

        sw.Stop();
        var title = await _page.TitleAsync();
        var status = response?.Status ?? 0;

        _logger.LogInformation("Navigated [{Status}] {Title} in {Ms}ms", status, title, sw.ElapsedMilliseconds);

        return new BrowserNavigateResult
        {
            Url = _page.Url,
            Title = title,
            HttpStatus = status,
            LoadTime = sw.Elapsed,
        };
    }

    public async Task<byte[]> ScreenshotAsync(
        string? cssSelector = null,
        bool fullPage = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Taking screenshot. Selector={Sel} FullPage={Full}", cssSelector, fullPage);

        var isJpeg = _options.ScreenshotFormat.Equals("jpeg", StringComparison.OrdinalIgnoreCase)
                  || _options.ScreenshotFormat.Equals("jpg",  StringComparison.OrdinalIgnoreCase);

        var screenshotType = isJpeg ? ScreenshotType.Jpeg : ScreenshotType.Png;

        if (!string.IsNullOrWhiteSpace(cssSelector))
        {
            var element = await _page.WaitForSelectorAsync(cssSelector, new PageWaitForSelectorOptions
            {
                Timeout = _options.DefaultTimeoutMs,
            }) ?? throw new BrowserElementNotFoundException($"Element not found: {cssSelector}");

            return await element.ScreenshotAsync(new ElementHandleScreenshotOptions
            {
                Type = screenshotType,
                Quality = isJpeg ? _options.JpegQuality : null,
            });
        }

        return await _page.ScreenshotAsync(new PageScreenshotOptions
        {
            FullPage = fullPage,
            Type = screenshotType,
            Quality = isJpeg ? _options.JpegQuality : null,
        });
    }

    public async Task<string> GetTextAsync(
        string? cssSelector = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cssSelector))
            return await _page.InnerTextAsync("body") ?? string.Empty;

        var element = await _page.WaitForSelectorAsync(cssSelector, new PageWaitForSelectorOptions
        {
            Timeout = _options.DefaultTimeoutMs,
        }) ?? throw new BrowserElementNotFoundException($"Element not found: {cssSelector}");

        return await element.InnerTextAsync() ?? string.Empty;
    }

    public async Task FillAsync(
        string cssSelector,
        string value,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Filling '{Selector}' with value (length={Len})", cssSelector, value.Length);

        await _page.WaitForSelectorAsync(cssSelector, new PageWaitForSelectorOptions
        {
            Timeout = _options.DefaultTimeoutMs,
            State = WaitForSelectorState.Visible,
        });

        await _page.FillAsync(cssSelector, value);
    }

    public async Task ClickAsync(
        string cssSelector,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Clicking '{Selector}'", cssSelector);

        await _page.WaitForSelectorAsync(cssSelector, new PageWaitForSelectorOptions
        {
            Timeout = _options.DefaultTimeoutMs,
            State = WaitForSelectorState.Visible,
        });

        await _page.ClickAsync(cssSelector);
    }

    public async Task SelectOptionAsync(
        string cssSelector,
        string value,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Selecting '{Value}' in '{Selector}'", value, cssSelector);
        await _page.SelectOptionAsync(cssSelector, value);
    }

    public async Task WaitForSelectorAsync(
        string cssSelector,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        await _page.WaitForSelectorAsync(cssSelector, new PageWaitForSelectorOptions
        {
            Timeout = timeoutMs ?? _options.DefaultTimeoutMs,
            State = WaitForSelectorState.Visible,
        });
    }

    public async Task<string?> EvaluateAsync(
        string script,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Evaluating JS ({Len} chars)", script.Length);
        var result = await _page.EvaluateAsync<object?>(script);
        return result?.ToString();
    }

    public virtual async ValueTask DisposeAsync()
    {
        try { await _page.CloseAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Error closing browser page (ignored)."); }
    }
}

// ============================================================================
//  Browser-specific exceptions
// ============================================================================

public sealed class BrowserTimeoutException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class BrowserElementNotFoundException(string message)
    : Exception(message);

public sealed class BrowserNotInitialisedException(string message)
    : Exception(message);
