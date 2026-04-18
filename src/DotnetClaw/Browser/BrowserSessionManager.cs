using DotnetClaw.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace DotnetClaw.Browser;

/// <summary>
/// Singleton service that owns the <see cref="IPlaywright"/> and <see cref="IBrowser"/>
/// instances for the lifetime of the DotnetClaw process.
///
/// Responsibilities:
///   • Lazy-initialise Playwright and launch the configured browser on first use
///   • Vend <see cref="IBrowserSession"/> instances (persistent or fresh per request)
///   • Dispose everything cleanly on shutdown
///
/// Persistent session mode (<see cref="BrowserOptions.PersistBrowserSession"/> = true):
///   The same page is reused across calls — cookies, local storage, and login
///   state survive between agent turns. The page is recreated if it's been closed.
///
/// Isolated session mode (<see cref="BrowserOptions.PersistBrowserSession"/> = false):
///   A fresh page is created for every call to <see cref="GetSessionAsync"/>.
///   The caller MUST dispose the session when finished.
/// </summary>
public sealed class BrowserSessionManager(
    IOptions<BrowserOptions> options,
    ILogger<BrowserSessionManager> logger) : IBrowserSessionManager, IHostedService, IAsyncDisposable
{
    private readonly BrowserOptions _options = options.Value;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _persistentPage;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;
    private bool _disposed;

    // ── IHostedService ────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Don't eagerly launch the browser at startup — lazy-init on first use.
        // This keeps startup fast and avoids the cost when the browser skill isn't used.
        logger.LogInformation(
            "BrowserSessionManager registered. Browser will launch on first use ({Type}, headless={H}).",
            _options.BrowserType, _options.Headless);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("BrowserSessionManager stopping — closing browser.");
        await DisposeAsync();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns an <see cref="IBrowserSession"/> ready for use.
    ///
    /// In persistent mode this always returns the same underlying page.
    /// In isolated mode a new page is created — the caller must dispose it.
    /// </summary>
    public async Task<IBrowserSession> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(BrowserSessionManager));
        await EnsureInitialisedAsync(cancellationToken);

        if (_options.PersistBrowserSession)
            return await GetOrCreatePersistentSessionAsync();

        // Isolated: fresh page, caller owns the lifecycle
        var freshPage = await _context!.NewPageAsync();
        return new PlaywrightBrowserSession(freshPage, _options, logger);
    }

    /// <summary>
    /// Check whether the browser has been launched without forcing initialisation.
    /// </summary>
    public bool IsRunning => _initialised && _browser?.IsConnected == true;

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task EnsureInitialisedAsync(CancellationToken cancellationToken)
    {
        if (_initialised) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialised) return;

            logger.LogInformation(
                "Launching {BrowserType} browser (headless={H}, viewport={W}x{H2})…",
                _options.BrowserType, _options.Headless,
                _options.ViewportWidth, _options.ViewportHeight);

            _playwright = await Playwright.CreateAsync();

            var browserType = _options.BrowserType.ToLowerInvariant() switch
            {
                "firefox"        => _playwright.Firefox,
                "webkit"         => _playwright.Webkit,
                _                => _playwright.Chromium,   // default
            };

            _browser = await browserType.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = _options.Headless,
                SlowMo   = _options.SlowMoMs > 0 ? _options.SlowMoMs : null,
            });

            var contextOptions = new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize
                {
                    Width  = _options.ViewportWidth,
                    Height = _options.ViewportHeight,
                },
            };

            if (!string.IsNullOrWhiteSpace(_options.UserAgent))
                contextOptions.UserAgent = _options.UserAgent;

            _context = await _browser.NewContextAsync(contextOptions);
            _initialised = true;

            logger.LogInformation("Browser ready: {BrowserType} v{Version}",
                _options.BrowserType, _browser.Version);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to launch {BrowserType} browser. " +
                "Ensure Playwright binaries are installed: playwright install {BrowserType}",
                _options.BrowserType, _options.BrowserType);
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<IBrowserSession> GetOrCreatePersistentSessionAsync()
    {
        // Recreate the page if it was closed externally
        if (_persistentPage is null || _persistentPage.IsClosed)
        {
            logger.LogDebug("Creating new persistent browser page.");
            _persistentPage = await _context!.NewPageAsync();
        }

        // Return a non-disposable wrapper so the persistent page isn't closed
        return new PersistentSessionWrapper(_persistentPage, _options, logger);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_persistentPage is not null && !_persistentPage.IsClosed)
                await _persistentPage.CloseAsync();
        }
        catch (Exception ex) { logger.LogDebug(ex, "Error closing persistent page."); }

        try { if (_context is not null) await _context.CloseAsync(); }
        catch (Exception ex) { logger.LogDebug(ex, "Error closing browser context."); }

        try { if (_browser is not null) await _browser.CloseAsync(); }
        catch (Exception ex) { logger.LogDebug(ex, "Error closing browser."); }

        _playwright?.Dispose();
        _initLock.Dispose();
    }
}

// ============================================================================
//  Persistent session wrapper
//  Prevents BrowserPlugin from accidentally closing the shared persistent page.
// ============================================================================

/// <summary>
/// Wraps a shared persistent <see cref="IPage"/> but makes <see cref="DisposeAsync"/>
/// a no-op — the page lifecycle is managed by <see cref="BrowserSessionManager"/>.
/// </summary>
internal sealed class PersistentSessionWrapper(IPage page, BrowserOptions options, ILogger logger)
    : PlaywrightBrowserSession(page, options, logger)
{
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask; // manager owns the page
}
