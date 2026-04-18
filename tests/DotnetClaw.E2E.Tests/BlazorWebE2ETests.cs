// Requires Playwright browser binaries. Run once before executing these tests:
//   pwsh bin/Debug/net10.0/playwright.ps1 install chromium

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using Xunit;

namespace DotnetClaw.E2E.Tests;

// ============================================================================
//  BlazorWebFactory — WebApplicationFactory that starts a real Kestrel port
// ============================================================================

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> that starts a second Kestrel
/// host bound to a random loopback port alongside the in-memory TestServer.
/// This is required so that Playwright's Chromium instance — which runs as a
/// real OS process — can reach the Blazor app over a real TCP socket.
/// </summary>
public sealed class BlazorWebFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private IHost? _kestrelHost;

    /// <summary>
    /// The base URL of the Kestrel server once started, e.g.
    /// "http://127.0.0.1:54321". Use this as the root URL in Playwright tests.
    /// </summary>
    public string ServerAddress { get; private set; } = string.Empty;

    // ── WebApplicationFactory overrides ──────────────────────────────────────

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Override the gateway client URL so WebGatewayClientService immediately
        // fails its connection attempt (port 1 is always refused) and retries
        // quietly in the background. This prevents test startup from hanging
        // while the hosted service waits for the real CLI gateway.
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GatewayClient:ServerUrl"]              = "ws://127.0.0.1:1/ws",
                ["GatewayClient:ReconnectDelaySeconds"]  = "1",
            });
        });

        // Use "Testing" environment so appsettings.Testing.json can override
        // production settings if it exists, and to distinguish from Development.
        builder.UseEnvironment("Testing");
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Build the in-memory TestServer host first (required by the base class)
        var testHost = builder.Build();

        // Configure a second identical host that binds Kestrel to a real port
        builder.ConfigureWebHost(wb =>
            wb.UseKestrel(o => o.Listen(IPAddress.Loopback, port: 0)));

        _kestrelHost = builder.Build();
        _kestrelHost.Start();

        // Resolve the dynamically assigned port from the Kestrel server features
        var server    = _kestrelHost.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("IServerAddressesFeature not available on Kestrel server.");
        ServerAddress = addresses.Addresses.Last();

        return testHost;
    }

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    public Task InitializeAsync() => Task.CompletedTask;

    public new async Task DisposeAsync()
    {
        if (_kestrelHost is not null)
            await _kestrelHost.StopAsync();

        await base.DisposeAsync();
    }
}

// ============================================================================
//  BlazorWebE2ETests — Playwright-based browser tests for the Blazor Web app
// ============================================================================

/// <summary>
/// Playwright end-to-end tests that launch a headless Chromium browser and
/// navigate the Blazor Server app via real HTTP requests to a Kestrel listener.
///
/// Prerequisites:
///   pwsh bin/Debug/net10.0/playwright.ps1 install chromium
/// </summary>
public sealed class BlazorWebE2ETests : IClassFixture<BlazorWebFactory>, IAsyncLifetime
{
    private readonly BlazorWebFactory _factory;
    private IPlaywright? _playwright;
    private IBrowser?   _browser;
    private IPage?      _page;

    public BlazorWebE2ETests(BlazorWebFactory factory)
    {
        _factory = factory;
    }

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        // Trigger server startup by requesting a client; the actual HTTP client
        // is unused — we only need Kestrel to have bound its port.
        _ = _factory.CreateClient();

        _playwright = await Playwright.CreateAsync();
        _browser    = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
        });
        _page = await _browser.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
            await _browser.CloseAsync();

        _playwright?.Dispose();
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the home/dashboard page loads successfully and includes
    /// the application name "DotnetClaw" in the page title or visible heading.
    /// </summary>
    [Fact]
    public async Task HomePage_Loads_HasExpectedTitle()
    {
        var homeUrl = _factory.ServerAddress.TrimEnd('/') + "/";
        await _page!.GotoAsync(homeUrl);

        // Either the HTML <title> or an <h1> on the page should contain the app name
        var title = await _page.TitleAsync();
        var hasNameInTitle = title.Contains("DotnetClaw", StringComparison.OrdinalIgnoreCase);

        bool hasNameInBody = false;
        if (!hasNameInTitle)
        {
            var bodyText = await _page.InnerTextAsync("body");
            hasNameInBody = bodyText.Contains("DotnetClaw", StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(
            hasNameInTitle || hasNameInBody,
            $"Expected 'DotnetClaw' in page title or body. Title was: '{title}'");
    }

    /// <summary>
    /// Verifies that the home page loads without throwing any JavaScript errors
    /// or unhandled exceptions in the browser context.
    /// </summary>
    [Fact]
    public async Task HomePage_Loads_NoJavaScriptErrors()
    {
        var jsErrors = new List<string>();

        // Capture any page-level JS exceptions
        _page!.PageError += (_, error) => jsErrors.Add(error);

        var homeUrl = _factory.ServerAddress.TrimEnd('/') + "/";
        await _page.GotoAsync(homeUrl);

        // Allow Blazor's SignalR circuit a moment to establish
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.Empty(jsErrors);
    }

    /// <summary>
    /// Verifies that the Chat page loads and renders a text input (or textarea)
    /// that the user can type into to send messages.
    /// </summary>
    [Fact]
    public async Task ChatPage_Loads_HasChatInput()
    {
        var chatUrl = _factory.ServerAddress.TrimEnd('/') + "/chat";
        await _page!.GotoAsync(chatUrl);

        // The chat page should render a text input or textarea for the message
        // Wait up to the default timeout for the element to appear after
        // Blazor's interactive server render mode activates.
        var inputSelector = "textarea, input[type='text'], input:not([type])";
        await _page.WaitForSelectorAsync(inputSelector);

        var inputVisible = await _page.IsVisibleAsync(inputSelector);
        Assert.True(inputVisible, "Expected a text input or textarea on the Chat page.");
    }

    /// <summary>
    /// Verifies that the Tasks page loads without a 500 error and renders
    /// visible page content (heading or empty-state message).
    /// </summary>
    [Fact]
    public async Task TasksPage_Loads_ShowsEmptyState()
    {
        var tasksUrl = _factory.ServerAddress.TrimEnd('/') + "/tasks";

        var response = await _page!.GotoAsync(tasksUrl);

        // Page must not return a 5xx server error
        Assert.NotNull(response);
        Assert.True(
            response!.Status < 500,
            $"Expected a non-5xx status code for /tasks, got {response.Status}");

        // The page should contain at least some readable text (heading or empty state)
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var bodyText = await _page.InnerTextAsync("body");
        Assert.False(
            string.IsNullOrWhiteSpace(bodyText),
            "Expected the Tasks page to render visible content.");
    }

    /// <summary>
    /// Verifies that navigating to a non-existent route displays a user-friendly
    /// 404 / "not found" message rather than an unhandled error page.
    /// </summary>
    [Fact]
    public async Task NotFoundPage_Returns404Content()
    {
        // The Blazor app uses UseStatusCodePagesWithReExecute("/not-found"),
        // so non-existent routes redirect to the /not-found Razor page.
        var badUrl = _factory.ServerAddress.TrimEnd('/') + "/nonexistent-route-xyz";
        await _page!.GotoAsync(badUrl);

        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var bodyText = await _page.InnerTextAsync("body");
        Assert.Contains("not found", bodyText, StringComparison.OrdinalIgnoreCase);
    }
}
