using DotnetClaw.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace DotnetClaw.Mcp;

// ============================================================================
//  MCP Connection Manager
//  Owns all live IMcpClient instances for the process lifetime.
// ============================================================================

/// <summary>
/// Snapshot of a connected (or failed) MCP server.
/// </summary>
public sealed record McpServerStatus
{
    public required string Name        { get; init; }
    public required string Description { get; init; }
    public required McpTransport Transport { get; init; }
    public required bool Connected     { get; init; }
    public string? Error               { get; init; }
    public int ToolCount               { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
}

/// <summary>
/// Manages the lifecycle of all configured MCP server connections.
///
    /// Responsibilities:
    ///   • Connect to every enabled server at startup (stdio process launch or SSE HTTP)
    ///   • Expose a thread-safe dictionary of live <see cref="IMcpClient"/> sessions
///   • Support per-server reconnect without restarting the whole process
///   • Dispose all clients gracefully on shutdown
///
/// The <see cref="McpKernelLoader"/> then imports each client's tools into the
/// Semantic Kernel as a named plugin at agent initialisation time.
/// </summary>
public sealed class McpConnectionManager(
    IOptions<McpOptions> options,
    ILogger<McpConnectionManager> logger) : IMcpConnectionManager, IHostedService, IAsyncDisposable
{
    private readonly McpOptions _options = options.Value;

    // Live clients indexed by server name (lowercase)
    private readonly Dictionary<string, IMcpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, McpServerStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    // ── IHostedService ────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var servers = _options.EnabledServers.ToList();

        if (servers.Count == 0)
        {
            logger.LogInformation("MCP: No servers configured. Add servers under DotnetClaw:Mcp:Servers.");
            return;
        }

        logger.LogInformation("MCP: Connecting to {Count} server(s)…", servers.Count);

        // Connect all servers in parallel — a single slow/failing server won't block others
        var tasks = servers.Select(s => ConnectServerAsync(s, cancellationToken));
        await Task.WhenAll(tasks);

        var connected = _statuses.Values.Count(s => s.Connected);
        logger.LogInformation("MCP: {Connected}/{Total} server(s) connected.", connected, servers.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("MCP: Disconnecting {Count} server(s)…", _clients.Count);
        await DisposeAsync();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Get a live client by server name.
    /// Returns null if the server is not connected or not configured.
    /// </summary>
    public IMcpClient? GetClient(string name) =>
        _clients.TryGetValue(name, out var client) ? client : null;

    /// <summary>All currently live clients, keyed by server name.</summary>
    public IReadOnlyDictionary<string, IMcpClient> AllClients => _clients;

    /// <summary>Status snapshot for all configured servers.</summary>
    public IReadOnlyCollection<McpServerStatus> GetStatuses() => _statuses.Values;

    /// <summary>
    /// Disconnect and reconnect a single server by name.
    /// Useful after config changes or transient failures.
    /// </summary>
    public async Task<McpServerStatus> ReconnectAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Dispose the old client if present
            if (_clients.TryGetValue(name, out var old))
            {
                _clients.Remove(name);
                try { await old.DisposeAsync(); }
                catch (Exception ex) { logger.LogDebug(ex, "MCP: Error disposing old client for '{Name}'.", name); }
            }

            var config = _options.EnabledServers.FirstOrDefault(
                s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (config is null)
            {
                var notFound = new McpServerStatus
                {
                    Name = name, Description = "", Transport = McpTransport.Stdio,
                    Connected = false, Error = $"Server '{name}' not found in configuration.",
                };
                _statuses[name] = notFound;
                return notFound;
            }

            return await ConnectServerAsync(config, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Connection logic ──────────────────────────────────────────────────────

    private async Task<McpServerStatus> ConnectServerAsync(
        McpServerConfig config,
        CancellationToken cancellationToken)
    {
        var timeoutSecs = config.ConnectionTimeoutSeconds ?? _options.ConnectionTimeoutSeconds;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSecs));

        logger.LogInformation(
            "MCP: Connecting to '{Name}' [{Transport}]…",
            config.Name, config.Transport);

        try
        {
            var client = config.Transport switch
            {
                McpTransport.Sse   => await ConnectSseAsync(config, cts.Token),
                McpTransport.Stdio => await ConnectStdioAsync(config, cts.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(config.Transport), config.Transport, null),
            };

            // List tools to verify connectivity and cache the count
            var tools = await client.ListToolsAsync(cancellationToken: cts.Token);
            var toolCount = tools.Count;

            logger.LogInformation(
                "MCP: '{Name}' connected — {Count} tool(s): {Tools}",
                config.Name, toolCount,
                string.Join(", ", tools.Select(t => t.Name)));

            var status = new McpServerStatus
            {
                Name = config.Name,
                Description = config.Description,
                Transport = config.Transport,
                Connected = true,
                ToolCount = toolCount,
                ConnectedAt = DateTimeOffset.UtcNow,
            };

            _clients[config.Name] = client;
            _statuses[config.Name] = status;
            return status;
        }
        catch (OperationCanceledException)
        {
            var err = $"Connection timed out after {timeoutSecs}s.";
            logger.LogWarning("MCP: '{Name}' — {Error}", config.Name, err);
            return RecordFailure(config, err);
        }
        catch (Exception ex)
        {
            var err = ex.Message;
            logger.LogError(ex, "MCP: Failed to connect to '{Name}'.", config.Name);
            return RecordFailure(config, err);
        }
    }

    private static async Task<IMcpClient> ConnectStdioAsync(
        McpServerConfig config,
        CancellationToken cancellationToken)
    {
        var transportOptions = new StdioClientTransportOptions
        {
            Command = config.Command,
            Arguments = config.Arguments,
            EnvironmentVariables = config.Environment.Count > 0
                ? config.Environment.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value)
                : null,
            WorkingDirectory = string.IsNullOrWhiteSpace(config.WorkingDirectory)
                ? null
                : config.WorkingDirectory,
        };

        var transport = new StdioClientTransport(transportOptions);
        var sdkClient = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        return new McpClientAdapter(sdkClient);
    }

    private static async Task<IMcpClient> ConnectSseAsync(
        McpServerConfig config,
        CancellationToken cancellationToken)
    {
        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = new Uri(config.Url),
        };

        var transport = new HttpClientTransport(transportOptions);
        var sdkClient = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        return new McpClientAdapter(sdkClient);
    }

    private McpServerStatus RecordFailure(McpServerConfig config, string error)
    {
        var status = new McpServerStatus
        {
            Name = config.Name,
            Description = config.Description,
            Transport = config.Transport,
            Connected = false,
            Error = error,
        };
        _statuses[config.Name] = status;
        return status;
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        foreach (var (name, client) in _clients)
        {
            try { await client.DisposeAsync(); }
            catch (Exception ex) { logger.LogDebug(ex, "MCP: Error disposing client '{Name}'.", name); }
        }
        _clients.Clear();
        _lock.Dispose();
    }
}
