using Microsoft.SemanticKernel;

namespace DotnetClaw.Mcp;

/// <summary>
/// Abstraction over <see cref="McpKernelLoader"/> to enable unit testing
/// without real Semantic Kernel / MCP infrastructure (Moq cannot mock sealed classes).
/// </summary>
public interface IMcpKernelLoader
{
    /// <summary>
    /// Import all live MCP client tools into <paramref name="kernel"/> as plugins.
    /// Safe to call multiple times — existing Mcp_ plugins are replaced.
    /// </summary>
    Task LoadAsync(Kernel kernel, CancellationToken cancellationToken = default);

    /// <summary>Reload a single server's plugin after reconnect.</summary>
    Task ReloadServerAsync(string serverName, Kernel kernel, CancellationToken cancellationToken = default);
}
