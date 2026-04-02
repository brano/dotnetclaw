using DotnetClaw.Config;
using DotnetClaw.Hub;
using DotnetClaw.Web.Components;
using DotnetClaw.Web.Gateway;
using DotnetClaw.Web.Services;
using DotnetClaw.Workspace;

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
