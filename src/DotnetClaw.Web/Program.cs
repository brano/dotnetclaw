using DotnetClaw.Config;
using DotnetClaw.Hub;
using DotnetClaw.Web.Components;
using DotnetClaw.Web.Gateway;
using DotnetClaw.Web.Services;
using DotnetClaw.Workspace;
using DotnetClaw.Workflowy.Config;
using DotnetClaw.Workflowy.Data;
using DotnetClaw.Workflowy.Engine;
using DotnetClaw.Workflowy.Plugin;
using Microsoft.EntityFrameworkCore;
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

// ── Options ───────────────────────────────────────────────────────────────────
builder.Services
    .Configure<DotnetClawOptions>(builder.Configuration.GetSection(DotnetClawOptions.SectionName))
    .Configure<HubOptions>(
        builder.Configuration.GetSection($"{DotnetClawOptions.SectionName}:{HubOptions.SectionName}"))
    .Configure<GatewayClientOptions>(
        builder.Configuration.GetSection(GatewayClientOptions.SectionName));

// ── Lightweight DotnetClaw services (no agent loop) ───────────────────────────
builder.Services
    .AddHttpClient<HubClient>()
        .Services
    // Workspace is still needed by TerminalService for hub install / reload
    .AddSingleton<WorkspaceLoader>();

// ── WebSocket Gateway client ──────────────────────────────────────────────────
builder.Services
    .AddSingleton<WebGatewayClientService>()
    .AddHostedService(sp => sp.GetRequiredService<WebGatewayClientService>());
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
    // ── Workflowy Workflow Engine ─────────────────────────────────────────
    .Configure<WorkflowyOptions>(
        builder.Configuration.GetSection($"{DotnetClawOptions.SectionName}:{WorkflowyOptions.SectionName}"))
    .AddDbContextFactory<WorkflowyDbContext>((sp, opts) =>
    {
        var o = sp.GetRequiredService<IOptions<WorkflowyOptions>>().Value;
        Directory.CreateDirectory(Path.GetDirectoryName(o.ResolvedDatabasePath)!);
        opts.UseSqlite($"Data Source={o.ResolvedDatabasePath}");
    })
    .AddSingleton<WorkflowLoader>()
    .AddSingleton<VariableResolver>()
    .AddSingleton<StepExecutor>()
    .AddSingleton<PipelineDispatcher>()
    .AddSingleton<WorkflowEngine>()
    .AddSingleton<WorkflowyApprovalService>()
    .AddSingleton<IApprovalNotifier>(sp => sp.GetRequiredService<WorkflowyApprovalService>())
    .AddSingleton<WorkflowyPlugin>()
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
