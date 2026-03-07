using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using McpSdkClient = ModelContextProtocol.Client.McpClient;

namespace DotnetClaw.Mcp;

/// <summary>
/// Thin adapter that wraps the SDK <see cref="McpSdkClient"/> and implements
/// <see cref="IMcpClient"/> so that callers are insulated from SDK API changes
/// and tests can supply lightweight fakes without depending on the SDK class hierarchy.
/// </summary>
internal sealed class McpClientAdapter(McpSdkClient client) : IMcpClient
{
    public Task<IList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken = default)
        => client.ListToolsAsync(cancellationToken: cancellationToken).AsTask();

    public async Task<CallToolResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default)
        => await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);

    public Task<IList<McpClientResource>> ListResourcesAsync(CancellationToken cancellationToken = default)
        => client.ListResourcesAsync(cancellationToken: cancellationToken).AsTask();

    public async Task<ReadResourceResult> ReadResourceAsync(
        string resourceUri,
        CancellationToken cancellationToken = default)
        => await client.ReadResourceAsync(resourceUri, cancellationToken: cancellationToken);

    public ValueTask DisposeAsync() => client.DisposeAsync();
}
