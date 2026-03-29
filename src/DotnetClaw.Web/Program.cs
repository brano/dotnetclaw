using DotnetClaw.Agents;
using DotnetClaw.Browser;
using DotnetClaw.Config;
using DotnetClaw.Hub;
using DotnetClaw.Mcp;
using DotnetClaw.Plugins;
using DotnetClaw.Telegram;
using DotnetClaw.UI;
using DotnetClaw.Web.Components;
using DotnetClaw.Web.Services;
using DotnetClaw.Workspace;
using Microsoft.Extensions.Options;

// ============================================================================
//  DotnetClaw.Web — Blazor Server entry point
// ============================================================================

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────────
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true);
}

builder.Configuration.AddEnvironmentVariables();

// ── Blazor ────────────────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ── Logging ───────────────────────────────────────────────────────────────────
builder.Services.AddLogging(logging =>
{
    logging.AddConsole();
    logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
});

// ── DotnetClaw Options ────────────────────────────────────────────────────────
builder.Services
    .Configure<DotnetClawOptions>(builder.Configuration.GetSection(DotnetClawOptions.SectionName))
    .Configure<CursorOptions>(
        builder.Configuration.GetSection($"{DotnetClawOptions.SectionName}:Cursor"))
    .Configure<TelegramOptions>(
        builder.Configuration.GetSection($"{DotnetClawOptions.SectionName}:Telegram"))
    .Configure<BrowserOptions>(
        builder.Configuration.GetSection($"{DotnetClawOptions.SectionName}:Browser"))
    .Configure<McpOptions>(
        builder.Configuration.GetSection($"{DotnetClawOptions.SectionName}:{McpOptions.SectionName}"))
    .Configure<HubOptions>(
        builder.Configuration.GetSection($"{DotnetClawOptions.SectionName}:{HubOptions.SectionName}"));

// ── DotnetClaw Core Services ──────────────────────────────────────────────────
builder.Services
    .AddHttpClient<HubClient>()
        .Services
    .AddSingleton<IConsoleRenderer, SpectreConsoleRenderer>()
    // Workspace
    .AddSingleton<WorkspaceLoader>()
    .AddSingleton<WorkspacePlugin>()
    // Plugins / skills
    .AddSingleton<ShellPlugin>()
    .AddSingleton<FileSystemPlugin>()
    .AddSingleton<DotnetPlugin>()
    // Cursor CLI
    .AddSingleton<ICursorProcessRunner, CursorProcessRunner>()
    .AddSingleton<CursorPlugin>()
    // Telegram Bot
    .AddHttpClient<ITelegramBotClient, TelegramBotClient>()
        .Services
    .AddSingleton<TelegramCommandRouter>(sp => new TelegramCommandRouter(
        sp.GetRequiredService<ClawAgentLoop>(),
        sp.GetRequiredService<CursorPlugin>(),
        sp.GetRequiredService<BrowserPlugin>(),
        sp.GetRequiredService<IOptions<TelegramOptions>>(),
        sp.GetRequiredService<ILogger<TelegramCommandRouter>>()))
    .AddSingleton<TelegramPlugin>()
    .AddHostedService<TelegramPollingService>()
    // Browser (Playwright)
    .AddSingleton<BrowserSessionManager>()
    .AddHostedService(sp => sp.GetRequiredService<BrowserSessionManager>())
    .AddSingleton<BrowserPlugin>()
    // MCP (Model Context Protocol)
    .AddSingleton<McpConnectionManager>()
    .AddHostedService(sp => sp.GetRequiredService<McpConnectionManager>())
    .AddSingleton<McpKernelLoader>()
    .AddSingleton<McpPlugin>()
    // Kernel
    .AddSingleton(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<DotnetClawOptions>>().Value;
        var logFactory = sp.GetRequiredService<ILoggerFactory>();
        return KernelFactory.Build(sp, opts, logFactory);
    })
    .AddSingleton<ClawAgentLoop>();

// ── Web UI Services ───────────────────────────────────────────────────────────
builder.Services
    .AddHttpContextAccessor()
    .AddSingleton<AppState>()
    .AddScoped<ChatService>()
    .AddScoped<AgentBridgeService>()
    .AddSingleton<TerminalService>();

var app = builder.Build();

// ── HTTP Pipeline ─────────────────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
