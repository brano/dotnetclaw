using DotnetClaw.Workspace;

namespace DotnetClaw.UI;

/// <summary>
/// IConsoleRenderer implementation that emits VT100/ANSI escape sequences
/// for rendering in a web terminal emulator (xterm.js).
///
/// All output is normalised to CRLF line endings because xterm.js in
/// non-convertEol mode requires explicit \r\n for newlines.
/// </summary>
public sealed class TerminalAnsiRenderer(Action<string> onOutput) : IConsoleRenderer
{
    // ── ANSI escape sequences ──────────────────────────────────────────────────
    private const string Reset        = "\x1b[0m";
    private const string Bold         = "\x1b[1m";
    private const string Yellow       = "\x1b[33m";
    private const string BoldYellow   = "\x1b[1;33m";
    private const string Green        = "\x1b[32m";
    private const string Red          = "\x1b[31m";
    private const string BoldRed      = "\x1b[1;31m";
    private const string Magenta      = "\x1b[35m";
    private const string BoldMagenta  = "\x1b[1;35m";
    private const string BrightWhite  = "\x1b[97m";
    private const string Grey         = "\x1b[90m";
    private const string ClearScreen  = "\x1b[2J\x1b[H";

    private void Write(string text) => onOutput(text);

    /// <summary>Normalize line endings to CRLF for xterm.js compatibility.</summary>
    private static string Crlf(string text)
        => text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    public void WriteBanner()
    {
        Write(ClearScreen);
        Write($"{Bold}{BoldMagenta}");
        Write(" ██████╗  ██████╗ ████████╗███╗   ██╗███████╗████████╗ ██████╗██╗      █████╗ ██╗    ██╗\r\n");
        Write(" ██╔══██╗██╔═══██╗╚══██╔══╝████╗  ██║██╔════╝╚══██╔══╝██╔════╝██║     ██╔══██╗██║    ██║\r\n");
        Write(" ██║  ██║██║   ██║   ██║   ██╔██╗ ██║█████╗     ██║   ██║     ██║     ███████║██║ █╗ ██║\r\n");
        Write(" ██║  ██║██║   ██║   ██║   ██║╚██╗██║██╔══╝     ██║   ██║     ██║     ██╔══██║██║███╗██║\r\n");
        Write(" ██████╔╝╚██████╔╝   ██║   ██║ ╚████║███████╗   ██║   ╚██████╗███████╗██║  ██║╚███╔███╔╝\r\n");
        Write($" ╚═════╝  ╚═════╝    ╚═╝   ╚═╝  ╚═══╝╚══════╝   ╚═╝    ╚═════╝╚══════╝╚═╝  ╚═╝ ╚══╝╚══╝{Reset}\r\n");
        Write("\r\n");
        Write($" {BoldMagenta}🦀 DotnetClaw{Reset}  {Grey}Personal AI Assistant in .NET — powered by Microsoft Semantic Kernel{Reset}\r\n");
        Write($" {Grey}🦞 OpenClaw-inspired agentic loop · Skills · Agents · Telegram bot channel{Reset}\r\n");
        Write("\r\n");
        Write($" {Grey}Type {BrightWhite}help{Reset}{Grey} for commands · {BrightWhite}reset{Reset}{Grey} to clear context · {BrightWhite}clear{Reset}{Grey} for fresh screen{Reset}\r\n");
        Write($" {Grey}──────────────────────────────────────────────────────────────────────────{Reset}\r\n");
        Write("\r\n");
    }

    public void BeginAssistantTurn()
        => Write($"\r\n{BoldMagenta}🦀 DotnetClaw:{Reset} ");

    public void WriteChunk(string text)
        => Write(Crlf(text));

    public void EndAssistantTurn()
        => Write("\r\n");

    public void WriteWarning(string message)
        => Write($"\r\n{Yellow}⚠  {Crlf(message)}{Reset}\r\n");

    public void WriteToolCall(string toolName, string input)
    {
        var preview = input.Length > 500 ? input[..500] + "…" : input;
        Write($"\r\n{BoldYellow}╭─ ⚡ Tool: {toolName}{Reset}\r\n");
        foreach (var line in Crlf(preview).Split("\r\n"))
            Write($"{Yellow}│{Reset}  {line}\r\n");
        Write($"{Yellow}╰──────────────────────────────────────────────────{Reset}\r\n");
    }

    public void WriteToolResult(string toolName, bool success, string preview)
    {
        var icon  = success ? "✅" : "❌";
        var color = success ? Green : Red;
        var safe  = preview.Length > 500 ? preview[..500] + "…" : preview;
        Write($"{color}{icon} {toolName}:{Reset} {Grey}{Crlf(safe)}{Reset}\r\n");
    }

    public void WriteError(string message)
        => Write($"\r\n{BoldRed}✖ Error:{Reset} {Red}{Crlf(message)}{Reset}\r\n");

    public void WriteWorkspaceStatus(WorkspaceLoadResult result)
    {
        Write($"\r\n{BoldMagenta}📂 Workspace{Reset}  {Grey}{result.WorkspacePath}{Reset}\r\n");

        if (result.IsEmpty)
        {
            Write($"  {Grey}No identity documents found.{Reset}\r\n");
        }
        else
        {
            foreach (var doc in result.Documents)
                Write($"  {Grey}├─{Reset}  {BrightWhite}{doc.Name}{Reset}  {Grey}({doc.Content.Length:N0} chars){Reset}\r\n");

            if (result.Skills.Count > 0)
                foreach (var skill in result.Skills)
                    Write($"  {Grey}├─{Reset}  {Yellow}{skill.SkillName}{Reset}  {Grey}(skill){Reset}\r\n");
        }
        Write("\r\n");
    }

    // Input is handled by xterm.js in the browser — this is a no-op in web context.
    public string PromptUser(string prompt = "> ") => string.Empty;
}
