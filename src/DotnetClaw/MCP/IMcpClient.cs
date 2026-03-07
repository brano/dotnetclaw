using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotnetClaw.Mcp;

/// <summary>
/// Abstraction over a connected MCP server session, exposing only the operations
/// needed by <see cref="Plugins.McpPlugin"/> and <see cref="McpKernelLoader"/>.
/// </summary>
public interface IMcpClient : IAsyncDisposable
{
    Task<IList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken = default);
    Task<CallToolResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default);
    Task<IList<McpClientResource>> ListResourcesAsync(CancellationToken cancellationToken = default);
    Task<ReadResourceResult> ReadResourceAsync(
        string resourceUri,
        CancellationToken cancellationToken = default);
}
