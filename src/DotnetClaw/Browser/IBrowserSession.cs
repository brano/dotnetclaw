namespace DotnetClaw.Browser;

// ============================================================================
//  Browser session abstraction
//  Decouples BrowserPlugin from Playwright types — makes unit testing possible
//  without spawning real browser processes.
// ============================================================================

/// <summary>
/// Represents a single browser tab / page session.
/// All operations are async and respect the cancellation token.
/// </summary>
public interface IBrowserSession : IAsyncDisposable
{
    /// <summary>URL the page is currently showing.</summary>
    string CurrentUrl { get; }

    /// <summary>Title of the current page.</summary>
    string CurrentTitle { get; }

    /// <summary>
    /// Navigate to <paramref name="url"/> and wait for the page to finish loading.
    /// </summary>
    Task<BrowserNavigateResult> NavigateAsync(
        string url,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Capture a screenshot of the whole page or a specific element.
    /// Returns the raw PNG or JPEG bytes.
    /// </summary>
    Task<byte[]> ScreenshotAsync(
        string? cssSelector = null,
        bool fullPage = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extract the visible text content of the page or an element.
    /// </summary>
    Task<string> GetTextAsync(
        string? cssSelector = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Type <paramref name="value"/> into the element matching <paramref name="cssSelector"/>.
    /// Clears any existing value first.
    /// </summary>
    Task FillAsync(
        string cssSelector,
        string value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Click the element matching <paramref name="cssSelector"/>.
    /// Waits for the element to be visible and stable before clicking.
    /// </summary>
    Task ClickAsync(
        string cssSelector,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Select an option in a <c>&lt;select&gt;</c> element by its visible label or value attribute.
    /// </summary>
    Task SelectOptionAsync(
        string cssSelector,
        string value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Wait for an element matching <paramref name="cssSelector"/> to appear in the DOM.
    /// </summary>
    Task WaitForSelectorAsync(
        string cssSelector,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute arbitrary JavaScript in the page context and return the result as a string.
    /// </summary>
    Task<string?> EvaluateAsync(
        string script,
        CancellationToken cancellationToken = default);
}

// ============================================================================
//  Result types
// ============================================================================

/// <summary>Result of a browser navigation.</summary>
public sealed record BrowserNavigateResult
{
    public required string Url { get; init; }
    public required string Title { get; init; }
    public required int HttpStatus { get; init; }
    public required TimeSpan LoadTime { get; init; }
    public bool Success => HttpStatus is >= 200 and < 400;

    public override string ToString() =>
        $"[{HttpStatus}] {Title} — {Url} ({LoadTime.TotalMilliseconds:F0} ms)";
}
