using System.Text;
using DotnetClaw.Config;
using DotnetClaw.Gateway;
using DotnetClaw.Hub;
using DotnetClaw.UI;
using DotnetClaw.Web.Gateway;
using DotnetClaw.Workspace;
using Microsoft.Extensions.Options;

namespace DotnetClaw.Web.Services;

// ============================================================================
//  TerminalService — shared web terminal session (ttyd / tmux style)
//
//  Architecture:
//    • Singleton — all browser tabs share the same terminal state and output
//    • OnOutput event — Terminal.razor instances subscribe and push ANSI text
//      to xterm.js over Blazor Server's own SignalR circuit
//    • Rolling buffer — new connections replay the last 64 KB of output
//    • SemaphoreSlim — serialises agent input (one command at a time)
//    • Agent turns are routed through WebGatewayClientService to the CLI process
// ============================================================================

public sealed class TerminalService : IDisposable
{
    // ── Rolling output buffer (64 KB) ──────────────────────────────────────────
    private const int MaxBufferBytes = 65_536;
    private readonly StringBuilder _buffer = new();

    // ── Concurrency ────────────────────────────────────────────────────────────
    private readonly SemaphoreSlim _inputLock = new(1, 1);
    private CancellationTokenSource? _currentCts;

    // ── Dependencies ───────────────────────────────────────────────────────────
    private readonly WebGatewayClientService _gateway;
    private readonly AppState _appState;
    private readonly HubClient _hub;
    private readonly WorkspaceLoader _workspace;
    private readonly DotnetClawOptions _coreOpts;
    private readonly ILogger<TerminalService> _logger;

    // ── ANSI renderer (stateful — maintains markdown streaming state per session) ──
    // Shared across all gateway message handlers so the MarkdownAnsiStreamFormatter
    // can correctly track bold/italic/code state across chunk boundaries.
    private readonly TerminalAnsiRenderer _ansiRenderer;

    // ── Gateway session ────────────────────────────────────────────────────────
    // The terminal uses a fixed session ID so CLI-side conversation history
    // is preserved across page reloads.
    private static readonly string TerminalSessionId = "terminal-" + Guid.NewGuid().ToString("N")[..8];
    private TaskCompletionSource? _agentTurnTcs;

    // ── State ──────────────────────────────────────────────────────────────────
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// Fired for every ANSI chunk written to the terminal.
    /// Terminal.razor instances subscribe and forward to xterm.js via JS interop.
    /// </summary>
    public event Action<string>? OnOutput;

    public TerminalService(
        WebGatewayClientService gateway,
        AppState appState,
        HubClient hub,
        WorkspaceLoader workspace,
        IOptions<DotnetClawOptions> coreOpts,
        ILogger<TerminalService> logger)
    {
        _gateway  = gateway;
        _appState = appState;
        _hub      = hub;
        _workspace = workspace;
        _coreOpts  = coreOpts.Value;
        _logger    = logger;

        // Emit is the sink — all ANSI text flows into the rolling buffer and fires OnOutput
        _ansiRenderer = new TerminalAnsiRenderer(Emit);

        _gateway.Subscribe(TerminalSessionId, HandleGatewayMessageAsync);
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Returns the full rolling output buffer for replaying to new connections.</summary>
    public string GetBufferedOutput() => _buffer.ToString();

    /// <summary>
    /// One-time initialisation: prints the banner, then emits the first prompt.
    /// Safe to call concurrently — only the first caller wins.
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        if (IsInitialized) return Task.CompletedTask;
        IsInitialized = true;

        // Same banner as the CLI — TerminalAnsiRenderer is the single source of truth
        _ansiRenderer.WriteBanner();

        if (_gateway.IsConnected)
            Emit($"\x1b[32m✓ Connected to gateway\x1b[0m  \x1b[90m(session: {TerminalSessionId[..16]}…)\x1b[0m\r\n");
        else
            Emit("\x1b[33m⚠ Gateway not connected — start the DotnetClaw CLI first.\x1b[0m\r\n");

        _logger.LogInformation("TerminalService: initialised (session={SessionId})", TerminalSessionId);
        EmitPrompt();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Process a single input line submitted by the user from xterm.js.
    /// Handles meta-commands locally; routes everything else to the CLI via gateway.
    /// </summary>
    public async Task HandleInputAsync(string line, CancellationToken ct = default)
    {
        if (!IsInitialized)
        {
            Emit("\r\n\x1b[33m[Terminal not ready — please wait…]\x1b[0m\r\n");
            return;
        }

        // Reject new input while agent is busy
        if (!await _inputLock.WaitAsync(0, ct))
        {
            Emit("\r\n\x1b[33m[Agent is busy — press Ctrl+C or click Cancel to interrupt]\x1b[0m\r\n");
            EmitPrompt();
            return;
        }

        try
        {
            _currentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var trimmed = line.Trim();

            if (string.IsNullOrEmpty(trimmed))
            {
                EmitPrompt();
                return;
            }

            // ── Meta-commands ──────────────────────────────────────────────────
            switch (trimmed.ToLowerInvariant())
            {
                case "help":
                    EmitHelp();
                    EmitPrompt();
                    return;

                case "clear":
                    Emit("\x1b[2J\x1b[H");
                    EmitPrompt();
                    return;

                case "reset":
                    Emit("\r\n\x1b[90mSending reset to CLI…\x1b[0m\r\n");
                    await _gateway.SendResetAsync(TerminalSessionId, _currentCts.Token);
                    Emit("\x1b[32m✓ Reset sent. Conversation will be cleared on the CLI.\x1b[0m\r\n");
                    EmitPrompt();
                    return;

                case "exit":
                    Emit("\r\n\x1b[33mClose this browser tab to exit.\x1b[0m\r\n");
                    EmitPrompt();
                    return;
            }

            // ── Hub commands ───────────────────────────────────────────────────
            if (trimmed.Equals("hub", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("hub ", StringComparison.OrdinalIgnoreCase))
            {
                await HandleHubCommandAsync(trimmed, _currentCts.Token);
                EmitPrompt();
                return;
            }

            // ── Route to CLI agent via gateway ─────────────────────────────────
            if (!_gateway.IsConnected)
            {
                Emit("\r\n\x1b[1;31m✖ Not connected to gateway.\x1b[0m Start the DotnetClaw CLI first.\r\n");
                EmitPrompt();
                return;
            }

            _appState.SetAgentRunning(true, "Processing…");
            _agentTurnTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                // Emit the "🦀 DotnetClaw>" prefix and reset the markdown streaming state
                _ansiRenderer.BeginAssistantTurn();
                await _gateway.SendChatMessageAsync(trimmed, TerminalSessionId, _currentCts.Token);

                // Wait for agent_response (or error) to arrive via the gateway handler
                using var reg = _currentCts.Token.Register(() => _agentTurnTcs.TrySetCanceled());
                await _agentTurnTcs.Task;
                _appState.RecordTurn();
            }
            finally
            {
                _agentTurnTcs = null;
                _appState.SetAgentRunning(false);
            }

            EmitPrompt();
        }
        catch (OperationCanceledException)
        {
            Emit("\r\n\x1b[33m[Cancelled]\x1b[0m\r\n");
            _appState.SetAgentRunning(false);
            EmitPrompt();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Terminal command failed");
            Emit($"\r\n\x1b[1;31m✖ Error:\x1b[0m \x1b[31m{ex.Message}\x1b[0m\r\n");
            _appState.SetAgentRunning(false);
            EmitPrompt();
        }
        finally
        {
            _currentCts?.Dispose();
            _currentCts = null;
            try { _inputLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Cancel the currently executing command (triggered by Ctrl+C).</summary>
    public void CancelCurrent() => _currentCts?.Cancel();

    // ── Gateway message handler ────────────────────────────────────────────────

    private Task HandleGatewayMessageAsync(GatewayMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.AgentChunk:
                // Feed raw text through the renderer so markdown is formatted to ANSI
                _ansiRenderer.WriteChunk(msg.Text ?? string.Empty);
                break;

            case MessageType.ToolCall:
                if (msg.Tool is not null)
                    _ansiRenderer.WriteToolCall(msg.Tool, msg.Input ?? string.Empty);
                break;

            case MessageType.ToolResult:
                if (msg.Tool is not null)
                    _ansiRenderer.WriteToolResult(msg.Tool, success: true, msg.Text ?? string.Empty);
                break;

            case MessageType.AgentResponse:
                // Flush any buffered markdown and emit the trailing newline
                _ansiRenderer.EndAssistantTurn();
                _agentTurnTcs?.TrySetResult();
                break;

            case MessageType.Error:
                _ansiRenderer.EndAssistantTurn();
                _ansiRenderer.WriteError(msg.Text ?? "error");
                _agentTurnTcs?.TrySetResult();
                break;

            case MessageType.ResetSession:
                Emit("\x1b[32m✓ Conversation reset.\x1b[0m\r\n");
                break;
        }

        return Task.CompletedTask;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private void Emit(string text)
    {
        _buffer.Append(text);

        // Keep rolling buffer within MaxBufferBytes
        if (_buffer.Length > MaxBufferBytes)
            _buffer.Remove(0, _buffer.Length - MaxBufferBytes);

        OnOutput?.Invoke(text);
    }

    private void EmitPrompt()
        => Emit("\r\n\x1b[1m\x1b[97mYou>\x1b[0m ");

    private void EmitHelp()
    {
        Emit("\r\n\x1b[1m\x1b[34mAvailable commands\x1b[0m\r\n\r\n");
        Emit("  \x1b[33mhelp\x1b[0m                   Show this help\r\n");
        Emit("  \x1b[33mreset\x1b[0m                  Reset CLI conversation history\r\n");
        Emit("  \x1b[33mclear\x1b[0m                  Clear the terminal screen\r\n");
        Emit("  \x1b[33mexit\x1b[0m                   Close terminal (close the browser tab)\r\n");
        Emit("\r\n");
        Emit("  \x1b[1m\x1b[34mDotnetClawHub commands\x1b[0m\r\n\r\n");
        Emit("  \x1b[33mhub\x1b[0m                    Show hub help\r\n");
        Emit("  \x1b[33mhub search\x1b[0m             List all skills available on the hub\r\n");
        Emit("  \x1b[33mhub search <query>\x1b[0m     Search hub skills by keyword\r\n");
        Emit("  \x1b[33mhub install <name>\x1b[0m     Install (or update) a skill from the hub\r\n");
        Emit("  \x1b[33mhub list\x1b[0m               List skills installed in your workspace\r\n");
        Emit("\r\n");
        Emit("  \x1b[90mAny other text is forwarded to the DotnetClaw AI agent via the gateway.\x1b[0m\r\n");
        Emit("\r\n");
        Emit("  \x1b[90mKeyboard shortcuts:\x1b[0m\r\n");
        Emit("  \x1b[90m  Ctrl+C   Cancel the current operation\x1b[0m\r\n");
        Emit("  \x1b[90m  Ctrl+L   Clear the terminal screen\x1b[0m\r\n");
        Emit("  \x1b[90m  Ctrl+U   Erase the current input line\x1b[0m\r\n");
        Emit("  \x1b[90m  ↑ / ↓   Navigate command history\x1b[0m\r\n");
        Emit("\r\n");
    }

    private async Task HandleHubCommandAsync(string input, CancellationToken ct)
    {
        if (!_hub.IsEnabled)
        {
            Emit("\r\n\x1b[1;31m✖ Hub is disabled.\x1b[0m Set DotnetClaw:Hub:Enabled=true in appsettings.json.\r\n");
            return;
        }

        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sub   = parts.Length > 1 ? parts[1].ToLowerInvariant() : string.Empty;

        switch (sub)
        {
            case "":
            case "help":
                Emit("\r\n\x1b[1m\x1b[34mDotnetClawHub commands\x1b[0m\r\n\r\n");
                Emit("  \x1b[33mhub search\x1b[0m             List all skills available on the hub\r\n");
                Emit("  \x1b[33mhub search <query>\x1b[0m     Search hub skills by keyword\r\n");
                Emit("  \x1b[33mhub install <name>\x1b[0m     Install (or update) a skill from the hub\r\n");
                Emit("  \x1b[33mhub list\x1b[0m               List skills installed in your workspace\r\n");
                Emit("\r\n");
                break;

            case "search":
            {
                var query = parts.Length > 2 ? string.Join(' ', parts[2..]) : null;
                Emit(string.IsNullOrWhiteSpace(query)
                    ? "\r\n\x1b[90mFetching skills from hub…\x1b[0m\r\n"
                    : $"\r\n\x1b[90mSearching hub for '{query}'…\x1b[0m\r\n");
                try
                {
                    var skills = await _hub.SearchAsync(query, ct);
                    if (skills.Count == 0)
                    {
                        Emit(string.IsNullOrWhiteSpace(query)
                            ? "\x1b[33mNo skills found on the hub.\x1b[0m\r\n"
                            : $"\x1b[33mNo skills matching '{query}'.\x1b[0m\r\n");
                        break;
                    }

                    Emit($"\x1b[32m{skills.Count} skill(s) found:\x1b[0m\r\n\r\n");
                    foreach (var s in skills)
                    {
                        Emit($"  \x1b[1m\x1b[97m{s.Name}\x1b[0m");
                        if (!string.IsNullOrWhiteSpace(s.Description))
                            Emit($"  \x1b[90m{s.Description}\x1b[0m");
                        Emit($"  \x1b[90m[{s.Downloads} installs]\x1b[0m\r\n");
                    }
                    Emit("\r\n");
                }
                catch (Exception ex)
                {
                    Emit($"\r\n\x1b[1;31m✖ Hub search failed:\x1b[0m \x1b[31m{ex.Message}\x1b[0m\r\n");
                }
                break;
            }

            case "install":
            {
                if (parts.Length < 3)
                {
                    Emit("\r\n\x1b[33mUsage: hub install <skill-name>\x1b[0m\r\n");
                    break;
                }
                var name = parts[2];
                Emit($"\r\n\x1b[90mInstalling skill '\x1b[97m{name}\x1b[90m' from hub…\x1b[0m\r\n");
                try
                {
                    var result = await _hub.InstallAsync(name, _coreOpts.ResolvedWorkspacePath, ct);
                    if (result.Success)
                    {
                        Emit($"\x1b[32m✓ Skill '{name}' installed.\x1b[0m\r\n");
                        Emit($"\x1b[90m  → {result.FilePath}\x1b[0m\r\n");
                        Emit("\x1b[90mReloading workspace…\x1b[0m\r\n");
                        await _workspace.ReloadAsync(ct);
                        Emit("\x1b[32m✓ Workspace reloaded — skill is now active.\x1b[0m\r\n");
                    }
                    else
                    {
                        Emit($"\r\n\x1b[1;31m✖ Install failed:\x1b[0m \x1b[31m{result.Error}\x1b[0m\r\n");
                    }
                }
                catch (Exception ex)
                {
                    Emit($"\r\n\x1b[1;31m✖ Hub install failed:\x1b[0m \x1b[31m{ex.Message}\x1b[0m\r\n");
                }
                break;
            }

            case "list":
            {
                var ws = await _workspace.LoadAsync(ct);
                if (ws.Skills.Count == 0)
                {
                    Emit("\r\n\x1b[33mNo skills installed in workspace.\x1b[0m\r\n");
                    break;
                }
                Emit($"\r\n\x1b[32m{ws.Skills.Count} installed skill(s):\x1b[0m\r\n\r\n");
                foreach (var s in ws.Skills)
                    Emit($"  \x1b[97m{s.SkillName}\x1b[0m\r\n");
                Emit("\r\n");
                break;
            }

            default:
                Emit($"\r\n\x1b[33mUnknown hub command '{sub}'. Type 'hub help' for usage.\x1b[0m\r\n");
                break;
        }
    }

    public void Dispose()
    {
        _gateway.Unsubscribe(TerminalSessionId);
        _currentCts?.Cancel();
        _currentCts?.Dispose();
        _inputLock.Dispose();
    }
}
