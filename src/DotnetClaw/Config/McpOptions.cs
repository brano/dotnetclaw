namespace DotnetClaw.Config;

// ============================================================================
//  MCP Client Configuration
//  Bound from appsettings.json under DotnetClaw:Mcp
// ============================================================================

/// <summary>
/// Transport type for connecting to an MCP server.
/// </summary>
public enum McpTransport
{
    /// <summary>
    /// Launch a local process and communicate over stdin/stdout.
    /// Most common for developer tools (filesystem, git, fetch, etc.).
    /// Requires <see cref="McpServerConfig.Command"/> to be set.
    /// </summary>
    Stdio,

    /// <summary>
    /// Connect to a remote MCP server over HTTP Server-Sent Events.
    /// Requires <see cref="McpServerConfig.Url"/> to be set.
    /// </summary>
    Sse,
}

/// <summary>
/// Configuration for a single MCP server connection.
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>
    /// Unique name used as the SK plugin name and for logging.
    /// Example: "filesystem", "github", "fetch", "postgres"
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable description surfaced in help text and status outputs.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Transport to use when connecting to this server.
    /// </summary>
    public McpTransport Transport { get; set; } = McpTransport.Stdio;

    // ── Stdio transport settings ──────────────────────────────────────────────

    /// <summary>
    /// [Stdio] Executable to launch.
    /// Examples: "npx", "uvx", "python", "/usr/local/bin/mcp-server-filesystem"
    /// </summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// [Stdio] Arguments passed to <see cref="Command"/>.
    /// Example: ["-y", "@modelcontextprotocol/server-filesystem", "/home/user/projects"]
    /// </summary>
    public List<string> Arguments { get; set; } = [];

    /// <summary>
    /// [Stdio] Additional environment variables injected into the server process.
    /// Use this for API keys, tokens, and credentials.
    /// Example: { "GITHUB_TOKEN": "ghp_..." }
    /// </summary>
    public Dictionary<string, string> Environment { get; set; } = [];

    /// <summary>
    /// [Stdio] Working directory for the server process.
    /// Leave empty to inherit DotnetClaw's working directory.
    /// </summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    // ── SSE transport settings ────────────────────────────────────────────────

    /// <summary>
    /// [SSE] Base URL of the remote MCP server.
    /// Example: "http://localhost:3000" or "https://mcp.example.com"
    /// </summary>
    public string Url { get; set; } = string.Empty;

    // ── Common settings ───────────────────────────────────────────────────────

    /// <summary>
    /// When false this server is skipped at startup — useful for temporarily
    /// disabling a server without removing its config.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Connection timeout in seconds. Overrides the global
    /// <see cref="McpOptions.ConnectionTimeoutSeconds"/> for this server.
    /// </summary>
    public int? ConnectionTimeoutSeconds { get; set; }
}

/// <summary>
/// Top-level MCP options — a list of server configs plus global settings.
/// </summary>
public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    /// <summary>
    /// MCP servers to connect to at startup.
    /// Each entry becomes a named SK plugin whose tools are available to the agent.
    /// </summary>
    public List<McpServerConfig> Servers { get; set; } = [];

    /// <summary>
    /// Default connection timeout in seconds for stdio server launch and SSE handshake.
    /// Individual servers can override via <see cref="McpServerConfig.ConnectionTimeoutSeconds"/>.
    /// </summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// When true, DotnetClaw logs all MCP tool call arguments and results at Debug level.
    /// Useful during development; disable in production to reduce noise.
    /// </summary>
    public bool LogToolCallDetails { get; set; } = false;

    /// <summary>Returns the configured servers that are currently enabled.</summary>
    public IEnumerable<McpServerConfig> EnabledServers =>
        Servers.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Name));
}
