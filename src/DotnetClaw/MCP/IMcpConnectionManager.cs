using ModelContextProtocol.Client;

namespace DotnetClaw.Mcp;

/// <summary>
/// Abstraction over <see cref="McpConnectionManager"/> to enable unit testing
/// without real MCP server connections (Moq cannot mock sealed classes).
/// </summary>
public interface IMcpConnectionManager
{
    /// <summary>Returns the <see cref="IMcpClient"/> for a named server, or <c>null</c> if not connected.</summary>
    IMcpClient? GetClient(string name);

    /// <summary>All currently connected MCP clients, keyed by server name.</summary>
    IReadOnlyDictionary<string, IMcpClient> AllClients { get; }

    /// <summary>Snapshot of connection status for every configured MCP server.</summary>
    IReadOnlyCollection<McpServerStatus> GetStatuses();

    /// <summary>Disconnect and reconnect a single named MCP server.</summary>
    Task<McpServerStatus> ReconnectAsync(string serverName, CancellationToken cancellationToken = default);
}
