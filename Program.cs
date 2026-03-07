using DotnetClaw.Agents;
using DotnetClaw.Browser;
using DotnetClaw.Config;
using DotnetClaw.Mcp;
using DotnetClaw.Plugins;
using DotnetClaw.Telegram;
using DotnetClaw.UI;
using DotnetClaw.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// ============================================================================
//  DotnetClaw — .NET 10 AI Assistant
//  OpenClaw-inspired agentic loop using Microsoft Semantic Kernel
// ============================================================================

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

// ── Dependency Injection ─────────────────────────────────────────────────────
var services = new ServiceCollection();

services.AddLogging(logging =>
{
    logging.AddConsole();
    logging.AddConfiguration(configuration.GetSection("Logging"));
});

services
    .Configure<DotnetClawOptions>(configuration.GetSection(DotnetClawOptions.SectionName))
    .Configure<CursorOptions>(
        configuration.GetSection($"{DotnetClawOptions.SectionName}:Cursor"))
    .Configure<TelegramOptions>(
        configuration.GetSection($"{DotnetClawOptions.SectionName}:Telegram"))
    .Configure<BrowserOptions>(
        configuration.GetSection($"{DotnetClawOptions.SectionName}:Browser"))
    .Configure<McpOptions>(
        configuration.GetSection($"{DotnetClawOptions.SectionName}:{McpOptions.SectionName}"))
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
    // Kernel (depends on plugins being registered first)
    .AddSingleton(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<DotnetClawOptions>>().Value;
        var logFactory = sp.GetRequiredService<ILoggerFactory>();
        return KernelFactory.Build(sp, opts, logFactory);
    })
    .AddSingleton<ClawAgentLoop>();

var provider = services.BuildServiceProvider();

// ── Start hosted services (Telegram polling) ──────────────────────────────────
var hostedServices = provider.GetServices<IHostedService>();
using var cts = new CancellationTokenSource();

foreach (var svc in hostedServices)
    await svc.StartAsync(cts.Token);

// ── Bootstrap ────────────────────────────────────────────────────────────────
var renderer  = provider.GetRequiredService<IConsoleRenderer>();
var agentLoop = provider.GetRequiredService<ClawAgentLoop>();
var workspace = provider.GetRequiredService<WorkspaceLoader>();
var telegramOpts = provider.GetRequiredService<IOptions<TelegramOptions>>().Value;

renderer.WriteBanner();

// Load workspace documents and display status table
var workspaceResult = await workspace.LoadAsync();
renderer.WriteWorkspaceStatus(workspaceResult);

// Show Telegram status
if (telegramOpts.Enabled && telegramOpts.IsConfigured)
    renderer.WriteWarning($"📱 Telegram bot active — {telegramOpts.AllowedChatIds.Count} authorised chat(s).");
else
    renderer.WriteWarning("📱 Telegram bot disabled. Set Enabled=true in appsettings.json to activate.");

// Initialise agent (async — injects workspace into system prompt)
await agentLoop.InitialiseAsync();

// ── REPL ─────────────────────────────────────────────────────────────────────
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

while (!cts.IsCancellationRequested)
{
    string input;
    try
    {
        input = renderer.PromptUser("you> ");
    }
    catch (OperationCanceledException)
    {
        break;
    }

    if (string.IsNullOrWhiteSpace(input))
        continue;

    // ── Built-in meta-commands ────────────────────────────────────────────
    switch (input.Trim().ToLowerInvariant())
    {
        case "exit":
        case "quit":
        case "bye":
            goto Done;

        case "reset":
        case "clear":
            await agentLoop.ResetAsync(cts.Token);
            renderer.WriteWarning("Conversation history cleared. Workspace reloaded.");
            continue;

        case "workspace":
        case "ws":
            var wsResult = await workspace.LoadAsync(cts.Token);
            renderer.WriteWorkspaceStatus(wsResult);
            continue;

        case "workspace reload":
        case "ws reload":
            var reloaded = await workspace.ReloadAsync(cts.Token);
            renderer.WriteWorkspaceStatus(reloaded);
            continue;

        case "prompt":
            renderer.WriteWarning("── Effective System Prompt ──────────────────────────────");
            Console.WriteLine(agentLoop.EffectiveSystemPrompt);
            renderer.WriteWarning("─────────────────────────────────────────────────────────");
            continue;

        case "history":
            var history = agentLoop.GetHistory();
            foreach (var msg in history)
                Console.WriteLine($"[{msg.Role}] {msg.Content}");
            continue;

        case "help":
            PrintHelp(renderer);
            continue;
    }

    try
    {
        await agentLoop.RunTurnAsync(input, cts.Token);
    }
    catch (OperationCanceledException)
    {
        renderer.WriteWarning("Turn cancelled.");
    }
    catch (Exception ex)
    {
        renderer.WriteError($"Unexpected error: {ex.Message}");
    }
}

Done:
// ── Graceful shutdown ─────────────────────────────────────────────────────────
foreach (var svc in hostedServices)
{
    try { await svc.StopAsync(CancellationToken.None); }
    catch { /* best-effort */ }
}

Console.WriteLine("👋  Goodbye from DotnetClaw!");

// ── Helpers ──────────────────────────────────────────────────────────────────
static void PrintHelp(IConsoleRenderer r)
{
    r.WriteWarning("""
        DotnetClaw Commands
        ───────────────────
          help              Show this message
          reset             Clear conversation history + reload workspace
          history           Print conversation history
          prompt            Show the effective system prompt (with workspace)
          workspace / ws    Show currently loaded workspace documents
          ws reload         Force reload workspace documents from disk
          exit              Quit DotnetClaw

        Available Skills
        ────────────────
          Shell      → run_command, list_directory, get_working_directory
          FileSystem → read_file, write_file, append_file, delete_file,
                       file_exists, find_files
          Dotnet     → find_csharp_projects, summarise_csharp_file,
                       get_nuget_packages
          Workspace  → list_workspace_docs, get_workspace_doc,
                       reload_workspace, get_workspace_context
          Cursor     → cursor_agent  (agent mode — edits files)
                       cursor_plan   (plan mode  — no file changes)
                       cursor_ask    (ask mode   — Q&A, read-only)
                       cursor_run    (low-level, explicit flags)
          Telegram   → send_telegram_message, send_telegram_notification
          Browser    → browser_navigate, browser_screenshot,
                       browser_screenshot_and_send, browser_get_text,
                       browser_fill, browser_click, browser_submit_form,
                       browser_evaluate
          Mcp        → mcp_list_servers, mcp_list_tools, mcp_call_tool,
                       mcp_reconnect, mcp_list_resources, mcp_read_resource
          Mcp_{name} → auto-imported tools from each connected MCP server

        Telegram Bot Commands (from Telegram)
        ──────────────────────────────────────
          /ask <question>    Ask DotnetClaw anything
          /plan <prompt>     Cursor plan mode (no edits)
          /agent <prompt>    Cursor agent mode (edits files!)
          /cursor_ask <q>    Cursor ask mode (Q&A)
          /reset             Clear conversation history
          /status            Show bot status
          /help              Show help
          <free text>        Same as /ask

        Environment Variables
        ─────────────────────
          DOTNETCLAW_PROVIDER   openai (default) | azure | anthropic
          OPENAI_API_KEY        Required for openai provider
          AZURE_OPENAI_ENDPOINT Required for azure provider
          AZURE_OPENAI_API_KEY  Required for azure provider
          TELEGRAM_BOT_TOKEN    Telegram bot token (overrides appsettings)
        """);
}
