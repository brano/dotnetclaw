using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using DotnetClaw.Config;
using DotnetClaw.UI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace DotnetClaw.Plugins;

/// <summary>
/// Cursor CLI skill — lets DotnetClaw invoke Cursor's headless coding agent
/// (<c>agent.exe</c> / <c>agent</c>) as a sub-process.
///
/// Supported modes (maps to Cursor's <c>--mode</c> flag):
///
///   agent  (default) — Cursor reads the codebase, plans, and applies edits autonomously.
///   plan             — Cursor produces a structured step-by-step plan, no file changes.
///   ask              — Cursor answers questions about the codebase, read-only.
///
/// CLI invocation format built by this plugin (matches cursor-agent CLI):
///   agent -p --mode=agent  "prompt"  [--model ...]  [--yes]  --trust "&lt;workspace&gt;"  [extraFlags]
///   agent -p --mode=plan   "prompt"  [--model ...]  --trust "&lt;workspace&gt;"  [extraFlags]
///   agent -p --mode=ask    "prompt"  [--model ...]  --trust "&lt;workspace&gt;"  [extraFlags]
/// </summary>
public sealed class CursorPlugin(
    ICursorProcessRunner processRunner,
    IOptions<CursorOptions> options,
    IConsoleRenderer renderer,
    ILogger<CursorPlugin> logger)
{
    private readonly CursorOptions _options = options.Value;

    // =========================================================================
    // Public kernel functions
    // =========================================================================

    /// <summary>
    /// Run a prompt through Cursor in <b>Agent mode</b>.
    /// The agent will read the target codebase, create a plan, and apply code edits.
    /// This is the most powerful mode — it can create, modify, and delete files.
    /// </summary>
    [KernelFunction("cursor_agent")]
    [Description(
        "Invoke the Cursor coding agent (agent.exe) in AGENT mode. " +
        "The agent autonomously reads the codebase, plans the changes, and edits files. " +
        "Use this when you want Cursor to implement a feature, fix a bug, or refactor code. " +
        "Returns the agent's output including what files were changed.")]
    public async Task<string> CursorAgentAsync(
        [Description(
            "The coding task or instruction for the Cursor agent. " +
            "Be specific: include file names, function names, or expected behaviour. " +
            "Example: 'Refactor AuthService to use IAuthRepository and add unit tests.'")]
        string prompt,

        [Description(
            "Path to the codebase folder the agent should work in. " +
            "Defaults to the DotnetClaw working directory if empty.")]
        string? workspacePath = null,

        [Description("Timeout in seconds. Defaults to CursorOptions.DefaultTimeoutSeconds. Max 1800.")]
        int? timeoutSeconds = null,

        CancellationToken cancellationToken = default)
    {
        var workspace = ResolveWorkspace(workspacePath);

        // Safety gate — Agent mode can mutate files, ask for confirmation
        if (_options.RequireConfirmationForAgentMode)
        {
            renderer.WriteToolCall("cursor_agent", $"Workspace: {workspace}\nPrompt: {prompt}");
            renderer.WriteWarning(
                "⚠️  Cursor AGENT mode will modify files in the workspace. " +
                "Type 'yes' to confirm or anything else to cancel:");

            var answer = Console.ReadLine()?.Trim() ?? string.Empty;
            if (!answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("User declined Cursor agent invocation.");
                return CursorResult.ConfirmationDenied(prompt, workspace).ToString();
            }
        }

        var result = await InvokeAsync(CursorMode.Agent, prompt, workspace, timeoutSeconds, cancellationToken);
        renderer.WriteToolResult("cursor_agent", result.Success, TruncateForDisplay(result.Stdout));
        return result.ToString();
    }

    /// <summary>
    /// Run a prompt through Cursor in <b>Plan mode</b>.
    /// The agent analyses the codebase and returns a structured plan — no files are changed.
    /// Use this to preview what Cursor would do before committing to Agent mode.
    /// </summary>
    [KernelFunction("cursor_plan")]
    [Description(
        "Invoke the Cursor coding agent (agent.exe) in PLAN mode. " +
        "The agent analyses the codebase and returns a structured, step-by-step implementation plan. " +
        "No files are modified. " +
        "Use this to preview what Cursor would do, or to produce a technical plan for review.")]
    public async Task<string> CursorPlanAsync(
        [Description(
            "Describe the feature, refactor, or bug fix you want Cursor to plan. " +
            "Example: 'Plan how to add JWT authentication to the existing ASP.NET Core API.'")]
        string prompt,

        [Description("Path to the codebase folder. Defaults to the working directory.")]
        string? workspacePath = null,

        [Description("Timeout in seconds. Max 1800.")]
        int? timeoutSeconds = null,

        CancellationToken cancellationToken = default)
    {
        var workspace = ResolveWorkspace(workspacePath);
        var result = await InvokeAsync(CursorMode.Plan, prompt, workspace, timeoutSeconds, cancellationToken);
        renderer.WriteToolResult("cursor_plan", result.Success, TruncateForDisplay(result.Stdout));
        return result.ToString();
    }

    /// <summary>
    /// Ask Cursor a question about a codebase in <b>Ask mode</b>.
    /// The agent reads the code and answers your question. No files are changed.
    /// </summary>
    [KernelFunction("cursor_ask")]
    [Description(
        "Invoke the Cursor coding agent (agent.exe) in ASK mode. " +
        "The agent reads the codebase and answers your question — it does NOT modify any files. " +
        "Use this to understand code: explain a class, trace a data flow, find where something is defined, etc.")]
    public async Task<string> CursorAskAsync(
        [Description(
            "The question to ask about the codebase. " +
            "Examples: 'How does the retry logic in HttpClientFactory work?' " +
            "or 'Where is the database connection string being set?'")]
        string question,

        [Description("Path to the codebase folder to ask about. Defaults to the working directory.")]
        string? workspacePath = null,

        [Description("Timeout in seconds. Max 1800.")]
        int? timeoutSeconds = null,

        CancellationToken cancellationToken = default)
    {
        var workspace = ResolveWorkspace(workspacePath);
        var result = await InvokeAsync(CursorMode.Ask, question, workspace, timeoutSeconds, cancellationToken);
        renderer.WriteToolResult("cursor_ask", result.Success, TruncateForDisplay(result.Stdout));
        return result.ToString();
    }

    /// <summary>
    /// Low-level Cursor runner — full control over all flags.
    /// Prefer <c>cursor_agent</c>, <c>cursor_plan</c>, or <c>cursor_ask</c> for most tasks.
    /// Use this when you need to pass custom flags or combine options not exposed by the typed functions.
    /// </summary>
    [KernelFunction("cursor_run")]
    [Description(
        "Low-level Cursor CLI runner with explicit control over mode and all flags. " +
        "Mode must be one of: 'agent', 'plan', 'ask'. " +
        "Prefer the typed functions (cursor_agent / cursor_plan / cursor_ask) unless you need " +
        "custom flags or a non-standard invocation.")]
    public async Task<string> CursorRunAsync(
        [Description("The prompt or question to send to Cursor.")]
        string prompt,

        [Description("Mode: 'agent' (makes changes), 'plan' (plan only), or 'ask' (Q&A, read-only).")]
        string mode,

        [Description("Path to the codebase folder. Defaults to the working directory.")]
        string? workspacePath = null,

        [Description("Optional: override the Cursor model, e.g. 'claude-3-5-sonnet' or 'gpt-4o'.")]
        string? model = null,

        [Description("Extra raw CLI flags to append verbatim, e.g. '--quiet --no-telemetry'.")]
        string? extraFlags = null,

        [Description("Timeout in seconds. Max 1800.")]
        int? timeoutSeconds = null,

        CancellationToken cancellationToken = default)
    {
        var parsedMode = ParseMode(mode);
        var workspace = ResolveWorkspace(workspacePath);

        var result = await InvokeAsync(
            parsedMode, prompt, workspace, timeoutSeconds, cancellationToken,
            modelOverride: model, extraFlagsOverride: extraFlags);

        renderer.WriteToolResult("cursor_run", result.Success, TruncateForDisplay(result.Stdout));
        return result.ToString();
    }

    // =========================================================================
    // Private helpers
    // =========================================================================

    private async Task<CursorResult> InvokeAsync(
        CursorMode mode,
        string prompt,
        string workspace,
        int? timeoutSeconds,
        CancellationToken cancellationToken,
        string? modelOverride = null,
        string? extraFlagsOverride = null)
    {
        var executablePath = ResolveExecutable();

        if (!ExecutableExists(executablePath))
            return CursorResult.NotFound(executablePath, mode, prompt);

        var arguments = BuildArguments(mode, prompt, workspace, modelOverride, extraFlagsOverride);
        var timeout = Math.Clamp(timeoutSeconds ?? _options.DefaultTimeoutSeconds, 5, 1800);

        var invocation = new CursorInvocation
        {
            ExecutablePath = executablePath,
            Arguments = arguments,
            WorkingDirectory = workspace,
            TimeoutSeconds = timeout,
            Mode = mode,
            Prompt = prompt,
        };

        logger.LogInformation(
            "Cursor invocation [{Mode}] workspace={Workspace} timeout={Timeout}s",
            mode, workspace, timeout);

        var output = await processRunner.RunAsync(invocation, cancellationToken);

        if (output.TimedOut)
            return CursorResult.Cancelled(prompt, mode, workspace);

        if (output.ProcessError)
            return CursorResult.ProcessError(mode, prompt, workspace, output.ProcessErrorMessage ?? "Unknown error");

        return new CursorResult
        {
            Mode = mode,
            Prompt = prompt,
            WorkspacePath = workspace,
            FullCommand = $"{executablePath} {arguments}",
            ExitCode = output.ExitCode,
            Success = output.ExitCode == 0,
            Stdout = output.Stdout,
            Stderr = output.Stderr,
            StartedAt = output.StartedAt,
            FinishedAt = output.FinishedAt,
        };
    }

    /// <summary>
    /// Build the argument string for the Cursor CLI.
    ///
    /// Final command structure (matches cursor-agent CLI):
    ///   agent -p --mode=&lt;mode&gt; "&lt;prompt&gt;" [--model &lt;model&gt;] [--yes] --trust "&lt;workspace&gt;" [extraFlags]
    /// </summary>
    private string BuildArguments(
        CursorMode mode,
        string prompt,
        string workspace,
        string? modelOverride,
        string? extraFlagsOverride)
    {
        var sb = new StringBuilder();

        // -p for non-interactive (prints response to console; required for headless/script use)
        sb.Append("-p");

        // Mode flag
        sb.Append($" --mode={ModeToFlag(mode)}");

        // Prompt — positional argument (cursor-agent expects this, not --prompt)
        sb.Append($" {ShellQuote(prompt)}");

        // Model override
        var model = modelOverride ?? _options.Model;
        if (!string.IsNullOrWhiteSpace(model))
            sb.Append($" --model {ShellQuote(model)}");

        // Auto-approve (agent mode only — plan/ask never mutate files)
        if (mode == CursorMode.Agent && _options.AutoApproveInAgentMode)
            sb.Append(" --yes");

        // --trust with workspace path (required for headless mode; trusts workspace without prompting)
        sb.Append($" --trust {ShellQuote(workspace)}");

        // Extra flags from config
        if (!string.IsNullOrWhiteSpace(_options.ExtraFlags))
            sb.Append($" {_options.ExtraFlags.Trim()}");

        // Per-call extra flags override
        if (!string.IsNullOrWhiteSpace(extraFlagsOverride))
            sb.Append($" {extraFlagsOverride.Trim()}");

        return sb.ToString();
    }

    private string ResolveExecutable()
    {
        var path = _options.ExecutablePath;

        // If just a bare name with no path separators, trust PATH resolution
        if (!path.Contains(Path.DirectorySeparatorChar) && !path.Contains(Path.AltDirectorySeparatorChar))
            return path;

        return Path.GetFullPath(path);
    }

    private static bool ExecutableExists(string path)
    {
        // For bare names (on PATH), we can't check without spawning — assume present
        if (!path.Contains(Path.DirectorySeparatorChar) && !path.Contains(Path.AltDirectorySeparatorChar))
            return true;

        // On Windows, also check with .exe extension if not already present
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(path + ".exe"))
            return true;

        return File.Exists(path);
    }

    private string ResolveWorkspace(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Path.GetFullPath(Directory.GetCurrentDirectory());

        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(path);
    }

    private static string ModeToFlag(CursorMode mode) => mode switch
    {
        CursorMode.Agent => "agent",
        CursorMode.Plan  => "plan",
        CursorMode.Ask   => "ask",
        _                => "agent",
    };

    private static CursorMode ParseMode(string mode) =>
        mode.Trim().ToLowerInvariant() switch
        {
            "agent"  => CursorMode.Agent,
            "plan"   => CursorMode.Plan,
            "ask"    => CursorMode.Ask,
            _ => throw new ArgumentException(
                $"Unknown Cursor mode '{mode}'. Valid values: agent, plan, ask.", nameof(mode))
        };

    /// <summary>
    /// Wraps a string in double-quotes and escapes inner double-quotes.
    /// Works for both bash (Linux/macOS) and cmd/PowerShell (Windows).
    /// </summary>
    private static string ShellQuote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string TruncateForDisplay(string text, int maxChars = 400) =>
        text.Length <= maxChars ? text : text[..maxChars] + $"\n… [{text.Length - maxChars:N0} chars truncated]";
}
