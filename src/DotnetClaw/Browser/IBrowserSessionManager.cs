namespace DotnetClaw.Browser;

/// <summary>
/// Abstraction over <see cref="BrowserSessionManager"/> to enable unit testing
/// without launching a real browser (Moq cannot mock sealed classes).
/// </summary>
public interface IBrowserSessionManager
{
    /// <summary>
    /// Returns an <see cref="IBrowserSession"/> ready for use.
    ///
    /// In persistent mode the same underlying page is reused across calls.
    /// In isolated mode a new page is created — the caller must dispose it.
    /// </summary>
    Task<IBrowserSession> GetSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the browser has been launched and is still connected.</summary>
    bool IsRunning { get; }
}
