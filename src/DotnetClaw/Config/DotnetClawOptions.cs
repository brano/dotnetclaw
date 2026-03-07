using System.ComponentModel.DataAnnotations;

namespace DotnetClaw.Config;

// ============================================================================
//  DotnetClawOptions — top-level application configuration
//
//  Bound from appsettings.json under the "DotnetClaw" key via:
//    services.Configure<DotnetClawOptions>(configuration.GetSection(DotnetClawOptions.SectionName))
//
//  All nested option classes (Cursor, Telegram, Browser, Mcp) are registered
//  separately so they can be injected independently with IOptions<T>.
//  This class holds their values inline as well for convenience when the full
//  options object is needed in one place (e.g. KernelFactory).
// ============================================================================

/// <summary>
/// Root configuration for DotnetClaw.
///
/// Bound from the <c>DotnetClaw</c> section in <c>appsettings.json</c>.
/// Override any setting via environment variables using the double-underscore
/// separator convention, e.g. <c>DotnetClaw__ModelId=gpt-4o-mini</c>.
/// </summary>
public sealed class DotnetClawOptions
{
    /// <summary>Configuration section name used in appsettings.json.</summary>
    public const string SectionName = "DotnetClaw";

    // =========================================================================
    // LLM / Agent
    // =========================================================================

    /// <summary>
    /// Model identifier passed to the LLM provider.
    ///
    /// OpenAI examples    : "gpt-4o", "gpt-4o-mini", "gpt-4-turbo"
    /// Azure OpenAI       : deployment name configured in your Azure portal
    /// Anthropic examples : "claude-3-5-sonnet-20241022", "claude-3-haiku-20240307"
    ///
    /// The active provider is controlled by the DOTNETCLAW_PROVIDER environment variable.
    /// </summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "ModelId is required.")]
    public string ModelId { get; set; } = "gpt-4o";

    /// <summary>
    /// Maximum number of agentic loop iterations before DotnetClaw forces a stop.
    ///
    /// Each iteration is one round of: model inference → optional tool calls → result injection.
    /// A higher value allows more complex multi-step tasks; a lower value guards against
    /// runaway loops consuming tokens.
    ///
    /// Range: 1–100. Default: 20.
    /// </summary>
    [Range(1, 100, ErrorMessage = "MaxIterations must be between 1 and 100.")]
    public int MaxIterations { get; set; } = 20;

    /// <summary>
    /// Base system prompt injected at the start of every conversation.
    ///
    /// This is prepended <em>before</em> any workspace identity documents (SOUL.md, AGENTS.md, etc.)
    /// and before the user's first message. Keep this short and high-level; use workspace
    /// documents for richer persona and project-specific context.
    ///
    /// Leave empty to use no base prompt (workspace documents take full responsibility).
    /// </summary>
    public string SystemPrompt { get; set; } =
        "You are DotnetClaw, a powerful AI assistant with access to shell, filesystem, " +
        "browser, and code execution tools. You are inspired by OpenClaw and designed to " +
        "help developers with complex tasks. Think step by step, use your tools effectively, " +
        "and always explain what you are doing.";

    // =========================================================================
    // Shell safety
    // =========================================================================

    /// <summary>
    /// Default working directory for shell commands executed by <c>ShellPlugin.run_command</c>.
    ///
    /// Relative paths are resolved from <see cref="AppContext.BaseDirectory"/>.
    /// Use <c>"."</c> to inherit the current working directory at launch.
    /// </summary>
    public string WorkingDirectory { get; set; } = ".";

    /// <summary>
    /// Allow-list of shell command prefixes. Only commands whose first token starts
    /// with one of these strings will be executed.
    ///
    /// <b>Empty list = developer mode (all commands allowed).</b>
    /// In production or shared environments, always populate this list.
    ///
    /// The block-list (<see cref="BlockedShellCommands"/>) is always checked first,
    /// regardless of this setting.
    ///
    /// Example: <c>["dotnet", "git", "npm"]</c>
    /// </summary>
    public List<string> AllowedShellCommands { get; set; } =
    [
        "ls", "dir", "cat", "echo", "pwd", "cd",
        "dotnet", "git", "npm", "node", "python",
        "grep", "find", "curl", "wget",
    ];

    /// <summary>
    /// Block-list of shell command patterns. Commands matching any entry in this list
    /// are always rejected, even if they appear in <see cref="AllowedShellCommands"/>.
    ///
    /// Matching is done as a prefix check on the full command string (after trimming).
    /// Add patterns here for any command that must never be executed regardless of context.
    ///
    /// Example: <c>["rm -rf /", "format", "del /f /s /q C:\\"]</c>
    /// </summary>
    public List<string> BlockedShellCommands { get; set; } =
    [
        "rm -rf /",
        "format",
        "del /f /s /q C:\\",
    ];

    /// <summary>
    /// Returns <c>true</c> if the shell is running in developer mode
    /// (no allow-list restrictions applied, only the block-list).
    /// </summary>
    public bool IsShellDevMode => AllowedShellCommands.Count == 0;

    // =========================================================================
    // Workspace identity documents
    // =========================================================================

    /// <summary>
    /// Path to the workspace folder containing Markdown identity documents.
    ///
    /// Relative paths are resolved from <see cref="AppContext.BaseDirectory"/>.
    /// The folder is expected to contain <c>SOUL.md</c>, <c>AGENTS.md</c>, etc.
    ///
    /// Default: <c>./workspace</c> (relative to the executable).
    /// </summary>
    public string WorkspacePath { get; set; } = "workspace";

    /// <summary>
    /// Ordered list of document basenames (without <c>.md</c>) that are injected
    /// into the system prompt first, in the order given.
    ///
    /// Any <c>*.md</c> files in the workspace folder that are <em>not</em> in this
    /// list are appended afterwards in alphabetical order.
    ///
    /// Default priority order:
    /// <c>SOUL → AGENTS → USER → CONTEXT → MEMORY → TOOLS → RULES</c>
    ///
    /// Leave empty to use the built-in default order above.
    /// </summary>
    public List<string> WorkspaceDocumentPriority { get; set; } =
    [
        "SOUL", "AGENTS", "USER", "CONTEXT", "MEMORY", "TOOLS", "RULES",
    ];

    /// <summary>
    /// When <c>true</c>, workspace documents are reloaded from disk automatically
    /// whenever the user runs the <c>reset</c> command.
    ///
    /// Useful during active document editing sessions where you want the agent to
    /// pick up changes to SOUL.md or CONTEXT.md without restarting.
    ///
    /// Default: <c>true</c>.
    /// </summary>
    public bool ReloadWorkspaceOnReset { get; set; } = true;

    /// <summary>
    /// Resolved absolute path to the workspace folder.
    /// </summary>
    public string ResolvedWorkspacePath =>
        Path.IsPathRooted(WorkspacePath)
            ? WorkspacePath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, WorkspacePath));

    // =========================================================================
    // Nested skill configurations
    //
    // These mirror the nested JSON sections and are populated automatically by
    // the configuration binder.  They are also registered individually as
    // IOptions<CursorOptions>, IOptions<TelegramOptions>, etc. in Program.cs
    // for injection into the specific plugin classes.
    // =========================================================================

    /// <summary>
    /// Settings for the Cursor CLI (<c>agent.exe</c>) coding skill.
    /// Bound from <c>DotnetClaw:Cursor</c>.
    /// </summary>
    public CursorOptions Cursor { get; set; } = new();

    /// <summary>
    /// Settings for the Telegram Bot integration.
    /// Bound from <c>DotnetClaw:Telegram</c>.
    /// </summary>
    public TelegramOptions Telegram { get; set; } = new();

    /// <summary>
    /// Settings for the Playwright headless browser skill.
    /// Bound from <c>DotnetClaw:Browser</c>.
    /// </summary>
    public BrowserOptions Browser { get; set; } = new();

    /// <summary>
    /// Settings for the MCP (Model Context Protocol) client skill.
    /// Bound from <c>DotnetClaw:Mcp</c>.
    /// </summary>
    public McpOptions Mcp { get; set; } = new();

    // =========================================================================
    // Computed / derived helpers
    // =========================================================================

    /// <summary>
    /// Returns a short human-readable summary of the active configuration,
    /// suitable for logging at startup.
    /// </summary>
    public string ToStartupSummary()
    {
        var mcpCount = Mcp.EnabledServers.Count();
        return
            $"Model={ModelId} | " +
            $"MaxIterations={MaxIterations} | " +
            $"ShellMode={( IsShellDevMode ? "DEV (unrestricted)" : $"{AllowedShellCommands.Count} allowed, {BlockedShellCommands.Count} blocked" )} | " +
            $"Workspace={WorkspacePath} | " +
            $"Telegram={( Telegram.Enabled ? "enabled" : "disabled" )} | " +
            $"Browser={Browser.BrowserType}/{( Browser.Headless ? "headless" : "headed" )} | " +
            $"MCP={mcpCount} server(s) enabled";
    }
}
