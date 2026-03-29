using DotnetClaw.Agents;
using DotnetClaw.Browser;
using DotnetClaw.Config;
using DotnetClaw.Hub;
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
    .Configure<HubOptions>(
        configuration.GetSection($"{DotnetClawOptions.SectionName}:{HubOptions.SectionName}"))
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
var hubClient = provider.GetRequiredService<HubClient>();
var coreOpts  = provider.GetRequiredService<IOptions<DotnetClawOptions>>().Value;
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

    // ── Hub commands (hub, hub search, hub install, hub list) ─────────────
    if (input.Trim().Equals("hub", StringComparison.OrdinalIgnoreCase) ||
        input.Trim().StartsWith("hub ", StringComparison.OrdinalIgnoreCase))
    {
        await HandleHubCommandAsync(input.Trim(), hubClient, workspace, coreOpts, renderer, cts.Token);
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

        DotnetClawHub Commands
        ──────────────────────
          hub               Show hub help
          hub search        List all skills available on the hub
          hub search <q>    Search hub skills by keyword
          hub install <n>   Install (or update) a skill from the hub
          hub list          List skills installed in your workspace

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

static async Task HandleHubCommandAsync(
    string input,
    DotnetClaw.Hub.HubClient hub,
    WorkspaceLoader workspace,
    DotnetClawOptions opts,
    IConsoleRenderer renderer,
    CancellationToken ct)
{
    if (!hub.IsEnabled)
    {
        renderer.WriteError("Hub is disabled. Set DotnetClaw:Hub:Enabled=true in appsettings.json.");
        return;
    }

    // Tokenise: ["hub", ...args]
    var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var sub   = parts.Length > 1 ? parts[1].ToLowerInvariant() : string.Empty;

    switch (sub)
    {
        case "":
        case "help":
            renderer.WriteWarning("""
                DotnetClawHub Commands
                ──────────────────────
                  hub search            List all skills available on the hub
                  hub search <query>    Search hub skills by keyword
                  hub install <name>    Install (or update) a skill from the hub
                  hub list              List skills installed in your workspace
                """);
            break;

        case "search":
        {
            var query = parts.Length > 2 ? string.Join(' ', parts[2..]) : null;
            try
            {
                var skills = await hub.SearchAsync(query, ct);
                if (skills.Count == 0)
                {
                    renderer.WriteWarning(string.IsNullOrWhiteSpace(query)
                        ? "No skills found on the hub."
                        : $"No skills matching '{query}'.");
                    break;
                }

                renderer.WriteWarning($"Found {skills.Count} skill(s) on hub:");
                foreach (var s in skills)
                    Console.WriteLine($"  {s.Name,-30} {s.Description}  [{s.Downloads} installs]");
            }
            catch (Exception ex)
            {
                renderer.WriteError($"Hub search failed: {ex.Message}");
            }
            break;
        }

        case "install":
        {
            if (parts.Length < 3)
            {
                renderer.WriteError("Usage: hub install <skill-name>");
                break;
            }
            var name = parts[2];
            renderer.WriteWarning($"Installing skill '{name}' from hub…");
            try
            {
                var result = await hub.InstallAsync(name, opts.ResolvedWorkspacePath, ct);
                if (result.Success)
                {
                    renderer.WriteWarning($"✓ Skill '{name}' installed to: {result.FilePath}");
                    await workspace.ReloadAsync(ct);
                    renderer.WriteWarning("Workspace reloaded — skill is now active.");
                }
                else
                {
                    renderer.WriteError($"✖ Install failed: {result.Error}");
                }
            }
            catch (Exception ex)
            {
                renderer.WriteError($"Hub install failed: {ex.Message}");
            }
            break;
        }

        case "list":
        {
            var ws = await workspace.LoadAsync(ct);
            if (ws.Skills.Count == 0)
            {
                renderer.WriteWarning("No skills installed in workspace.");
                break;
            }
            renderer.WriteWarning($"{ws.Skills.Count} installed skill(s):");
            foreach (var s in ws.Skills)
                Console.WriteLine($"  {s.SkillName}");
            break;
        }

        default:
            renderer.WriteError($"Unknown hub command '{sub}'. Type 'hub help' for usage.");
            break;
    }
}
