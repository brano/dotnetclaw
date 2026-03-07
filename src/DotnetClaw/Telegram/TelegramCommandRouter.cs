using DotnetClaw.Agents;
using DotnetClaw.Config;
using DotnetClaw.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetClaw.Telegram;

// ============================================================================
//  Command routing
// ============================================================================

/// <summary>
/// All recognised Telegram bot commands.
/// </summary>
public enum TelegramCommand
{
    /// <summary>Free-form text — passed directly to the DotnetClaw agent loop.</summary>
    FreeText,

    /// <summary>/ask &lt;question&gt; — send to agent loop in standard Q&amp;A mode.</summary>
    Ask,

    /// <summary>/plan &lt;prompt&gt; — invoke Cursor in plan mode (no file edits).</summary>
    CursorPlan,

    /// <summary>/agent &lt;prompt&gt; — invoke Cursor in agent mode (file edits).</summary>
    CursorAgent,

    /// <summary>/cursor_ask &lt;question&gt; — invoke Cursor in ask/Q&amp;A mode.</summary>
    CursorAsk,

    /// <summary>/goto &lt;url&gt; — navigate the browser to a URL.</summary>
    BrowserGoto,

    /// <summary>/screenshot [selector] — take a screenshot and send it to Telegram.</summary>
    BrowserScreenshot,

    /// <summary>/reset — clear the DotnetClaw conversation history.</summary>
    Reset,

    /// <summary>/status — return the bot's current state.</summary>
    Status,

    /// <summary>/help — list available commands.</summary>
    Help,

    /// <summary>Command was recognised but is missing required arguments.</summary>
    MissingArgs,

    /// <summary>Unknown /command — not in the recognised set.</summary>
    Unknown,
}

/// <summary>A parsed Telegram command with its argument payload.</summary>
public sealed record ParsedCommand(
    TelegramCommand Command,
    string Argument,           // everything after the command name
    string RawText);           // original unmodified message text

/// <summary>
/// Parses incoming Telegram message text into typed commands and
/// dispatches them to <see cref="ClawAgentLoop"/> or <see cref="CursorPlugin"/>.
/// </summary>
public sealed class TelegramCommandRouter(
    ClawAgentLoop agentLoop,
    CursorPlugin cursorPlugin,
    DotnetClaw.Plugins.BrowserPlugin browserPlugin,
    IOptions<TelegramOptions> options,
    ILogger<TelegramCommandRouter> logger)
{
    private readonly TelegramOptions _options = options.Value;

    // ── Known commands needing an argument ──────────────────────────────────

    private static readonly HashSet<string> _requiresArg = new(StringComparer.OrdinalIgnoreCase)
    {
        "/ask", "/plan", "/agent", "/cursor_ask", "/cursor_plan", "/cursor_agent", "/goto"
        // /screenshot does NOT require an arg — fires with defaults if none given
    };

    // ── Parse ────────────────────────────────────────────────────────────────

    /// <summary>Parse raw message text into a typed <see cref="ParsedCommand"/>.</summary>
    public static ParsedCommand Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new ParsedCommand(TelegramCommand.FreeText, string.Empty, text);

        var trimmed = text.Trim();

        // Not a command — treat as free-text for the agent loop
        if (!trimmed.StartsWith('/'))
            return new ParsedCommand(TelegramCommand.FreeText, trimmed, text);

        // Split "/command@BotName arg1 arg2..." → command + args
        // Strip optional @BotName suffix that Telegram appends in group chats
        var space = trimmed.IndexOf(' ');
        var commandPart = space >= 0 ? trimmed[..space] : trimmed;
        var argument = space >= 0 ? trimmed[(space + 1)..].Trim() : string.Empty;

        // Strip @BotName from command, e.g. /ask@MyBot → /ask
        var atSign = commandPart.IndexOf('@');
        if (atSign >= 0) commandPart = commandPart[..atSign];

        var cmd = commandPart.ToLowerInvariant() switch
        {
            "/ask"          => TelegramCommand.Ask,
            "/plan"         => TelegramCommand.CursorPlan,
            "/cursor_plan"  => TelegramCommand.CursorPlan,
            "/agent"        => TelegramCommand.CursorAgent,
            "/cursor_agent" => TelegramCommand.CursorAgent,
            "/cursor_ask"   => TelegramCommand.CursorAsk,
            "/goto"         => TelegramCommand.BrowserGoto,
            "/screenshot"   => TelegramCommand.BrowserScreenshot,
            "/reset"        => TelegramCommand.Reset,
            "/status"       => TelegramCommand.Status,
            "/help"         => TelegramCommand.Help,
            "/start"        => TelegramCommand.Help,
            _ => TelegramCommand.Unknown,
        };

        // Check for missing required arguments
        if (cmd != TelegramCommand.Unknown
            && _requiresArg.Contains(commandPart)
            && string.IsNullOrWhiteSpace(argument))
        {
            return new ParsedCommand(TelegramCommand.MissingArgs, commandPart, text);
        }

        return new ParsedCommand(cmd, argument, text);
    }

    // ── Dispatch ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Dispatch a parsed command and return the response text to send back
    /// to the Telegram user. Never throws — exceptions are caught and returned
    /// as error messages.
    /// </summary>
    public async Task<string> DispatchAsync(
        ParsedCommand cmd,
        long chatId,
        string? workspacePath = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Dispatching Telegram command {Cmd} for chat {ChatId}",
            cmd.Command, chatId);

        try
        {
            return cmd.Command switch
            {
                TelegramCommand.FreeText         => await RunAgentAsync(cmd.Argument, cancellationToken),
                TelegramCommand.Ask              => await RunAgentAsync(cmd.Argument, cancellationToken),
                TelegramCommand.CursorPlan       => await RunCursorPlanAsync(cmd.Argument, workspacePath, cancellationToken),
                TelegramCommand.CursorAgent      => await RunCursorAgentAsync(cmd.Argument, workspacePath, cancellationToken),
                TelegramCommand.CursorAsk        => await RunCursorAskAsync(cmd.Argument, workspacePath, cancellationToken),
                TelegramCommand.BrowserGoto      => await RunBrowserGotoAsync(cmd.Argument, chatId, cancellationToken),
                TelegramCommand.BrowserScreenshot => await RunBrowserScreenshotAsync(cmd.Argument, chatId, cancellationToken),
                TelegramCommand.Reset            => await ResetAsync(cancellationToken),
                TelegramCommand.Status           => BuildStatusMessage(),
                TelegramCommand.Help             => BuildHelpMessage(),
                TelegramCommand.MissingArgs      => $"⚠️ `{cmd.Argument}` requires a prompt. Example:\n`{cmd.Argument} <your prompt here>`",
                TelegramCommand.Unknown          => $"❓ Unknown command `{cmd.RawText.Split(' ')[0]}`. Use /help for the list.",
                _ => "🤔 Unhandled command.",
            };
        }
        catch (OperationCanceledException)
        {
            return "⏱ Request was cancelled.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error dispatching Telegram command {Cmd}", cmd.Command);
            return $"❌ Error: {EscapeMarkdown(ex.Message)}";
        }
    }

    // ── Private dispatch helpers ──────────────────────────────────────────────

    private async Task<string> RunAgentAsync(string prompt, CancellationToken ct)
    {
        var collector = new ResponseCollector();
        await agentLoop.RunTurnAsync(prompt, ct, collector);
        return collector.GetResponse();
    }

    private async Task<string> RunCursorPlanAsync(string prompt, string? workspace, CancellationToken ct)
    {
        var result = await cursorPlugin.CursorPlanAsync(prompt, workspace, cancellationToken: ct);
        return $"📋 *Cursor Plan*\n\n```\n{result}\n```";
    }

    private async Task<string> RunCursorAgentAsync(string prompt, string? workspace, CancellationToken ct)
    {
        var result = await cursorPlugin.CursorAgentAsync(prompt, workspace, cancellationToken: ct);
        return $"🤖 *Cursor Agent*\n\n```\n{result}\n```";
    }

    private async Task<string> RunCursorAskAsync(string question, string? workspace, CancellationToken ct)
    {
        var result = await cursorPlugin.CursorAskAsync(question, workspace, cancellationToken: ct);
        return $"💬 *Cursor Ask*\n\n{result}";
    }

    private async Task<string> ResetAsync(CancellationToken ct)
    {
        await agentLoop.ResetAsync(ct);
        return "🔄 Conversation history cleared and workspace reloaded\\.";
    }

    private async Task<string> RunBrowserGotoAsync(string url, long chatId, CancellationToken ct)
    {
        var navResult = await browserPlugin.NavigateAsync(url, cancellationToken: ct);

        // After navigating, automatically send a screenshot to give visual confirmation
        var screenshot = await browserPlugin.ScreenshotAndSendAsync(
            chatId: chatId,
            caption: $"📸 Navigated to: {url}",
            cancellationToken: ct);

        return $"{navResult}\n{screenshot}";
    }

    private async Task<string> RunBrowserScreenshotAsync(string argument, long chatId, CancellationToken ct)
    {
        // argument is an optional CSS selector — empty means full viewport
        var selector = string.IsNullOrWhiteSpace(argument) ? null : argument.Trim();
        return await browserPlugin.ScreenshotAndSendAsync(
            cssSelector: selector,
            chatId: chatId,
            cancellationToken: ct);
    }

    private static string BuildStatusMessage() =>
        """
        🦀 *DotnetClaw Status*

        ✅ Bot is online and running
        ✅ Agent loop is ready
        ✅ Skills: Shell, FileSystem, Dotnet, Workspace, Cursor, Browser

        Use /help to see available commands\.
        """;

    private static string BuildHelpMessage() =>
        """
        🦀 *DotnetClaw Bot Commands*

        *General*
        /ask \<question\> — Ask DotnetClaw anything
        \<free text\>     — Same as /ask, just type your message

        *Browser*
        /goto \<url\>        — Navigate browser \+ send screenshot
        /screenshot          — Screenshot current page
        /screenshot \<sel\>  — Screenshot a CSS element

        *Cursor CLI*
        /plan \<prompt\>       — Plan a coding task \(no file changes\)
        /cursor\_ask \<q\>      — Ask Cursor about a codebase
        /agent \<prompt\>      — Run Cursor agent \(edits files\!\)

        *Session*
        /reset   — Clear conversation history \+ reload workspace
        /status  — Show bot status
        /help    — Show this message

        *Tips*
        • /goto sends a screenshot automatically after navigating\.
        • /screenshot accepts an optional CSS selector\.
        • Cursor commands default to the configured working directory\.
        • The bot only responds to authorised chat IDs\.
        """;

    // ── Utilities ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Escape special characters for Telegram MarkdownV2.
    /// Required chars: _ * [ ] ( ) ~ ` > # + - = | { } . !
    /// </summary>
    public static string EscapeMarkdown(string text) =>
        text
            .Replace("\\", "\\\\")
            .Replace("_", "\\_")
            .Replace("*", "\\*")
            .Replace("[", "\\[")
            .Replace("]", "\\]")
            .Replace("(", "\\(")
            .Replace(")", "\\)")
            .Replace("~", "\\~")
            .Replace("`", "\\`")
            .Replace(">", "\\>")
            .Replace("#", "\\#")
            .Replace("+", "\\+")
            .Replace("-", "\\-")
            .Replace("=", "\\=")
            .Replace("|", "\\|")
            .Replace("{", "\\{")
            .Replace("}", "\\}")
            .Replace(".", "\\.")
            .Replace("!", "\\!");
}

// ============================================================================
//  Response collector — captures streamed agent output into a string
// ============================================================================

/// <summary>
/// Adapter that implements <see cref="IConsoleRenderer"/> as a string buffer,
/// allowing <see cref="ClawAgentLoop.RunTurnAsync"/> to be called with a
/// Telegram-bound output sink instead of writing to the terminal.
/// </summary>
internal sealed class ResponseCollector : DotnetClaw.UI.IConsoleRenderer
{
    private readonly System.Text.StringBuilder _sb = new();

    public void WriteChunk(string text) => _sb.Append(text);
    public void BeginAssistantTurn() { }
    public void EndAssistantTurn() { }
    public void WriteWarning(string message) => _sb.AppendLine(message);
    public void WriteError(string message) => _sb.AppendLine($"Error: {message}");
    public void WriteToolCall(string toolName, string input) { } // silent for Telegram
    public void WriteToolResult(string toolName, bool success, string preview) { }
    public void WriteBanner() { }
    public void WriteWorkspaceStatus(DotnetClaw.Workspace.WorkspaceLoadResult result) { }
    public string PromptUser(string prompt = "> ") => string.Empty;

    public string GetResponse()
    {
        var text = _sb.ToString().Trim();
        return string.IsNullOrEmpty(text) ? "_(no response)_" : text;
    }
}
