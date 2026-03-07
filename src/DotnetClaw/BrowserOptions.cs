namespace DotnetClaw.Config;

/// <summary>
/// Configuration for the Playwright Browser skill.
/// Bound from <c>appsettings.json</c> under the <c>DotnetClaw:Browser</c> key.
///
/// First-time setup — install browser binaries once:
///   dotnet tool install --global Microsoft.Playwright.CLI
///   playwright install chromium
///
/// Or via the project:
///   dotnet run --project src/DotnetClaw -- playwright install chromium
/// </summary>
public sealed class BrowserOptions
{
    /// <summary>
    /// Browser engine to use.
    /// Supported values: <c>chromium</c> (default), <c>firefox</c>, <c>webkit</c>.
    /// </summary>
    public string BrowserType { get; set; } = "chromium";

    /// <summary>
    /// Run browser in headless mode (no visible window).
    /// Set <c>false</c> for debugging to see what the browser is doing.
    /// </summary>
    public bool Headless { get; set; } = true;

    /// <summary>
    /// Default timeout in milliseconds for navigation and element waits.
    /// Playwright default is 30 000 ms.
    /// </summary>
    public int DefaultTimeoutMs { get; set; } = 30_000;

    /// <summary>
    /// Directory where screenshots are saved.
    /// Relative paths are resolved from <see cref="AppContext.BaseDirectory"/>.
    /// Defaults to <c>./screenshots</c>.
    /// </summary>
    public string ScreenshotDirectory { get; set; } = "screenshots";

    /// <summary>
    /// Screenshot image format: <c>png</c> (default) or <c>jpeg</c>.
    /// PNG is lossless and preferred for code/text; JPEG is smaller for photos.
    /// </summary>
    public string ScreenshotFormat { get; set; } = "png";

    /// <summary>
    /// JPEG quality (1–100). Only used when <see cref="ScreenshotFormat"/> is <c>jpeg</c>.
    /// </summary>
    public int JpegQuality { get; set; } = 90;

    /// <summary>
    /// When <c>true</c>, the same <see cref="Microsoft.Playwright.IPage"/> instance is
    /// reused across calls in the same DotnetClaw session (cookies/login state persists).
    /// When <c>false</c>, a fresh page is created for every navigation (isolated, clean state).
    /// </summary>
    public bool PersistBrowserSession { get; set; } = true;

    /// <summary>
    /// Optional user-agent string override.
    /// Leave empty to use Playwright's default.
    /// </summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>
    /// Viewport width in pixels. Default 1280.
    /// </summary>
    public int ViewportWidth { get; set; } = 1280;

    /// <summary>
    /// Viewport height in pixels. Default 800.
    /// </summary>
    public int ViewportHeight { get; set; } = 800;

    /// <summary>
    /// Slow down all Playwright operations by this many milliseconds.
    /// Useful for watching what the browser is doing. Set 0 in production.
    /// </summary>
    public int SlowMoMs { get; set; } = 0;
}
