using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;

namespace DotnetClaw.Mcp;

// ============================================================================
//  MCP → Semantic Kernel bridge
//
//  Converts every live IMcpClient into a named KernelPlugin whose functions
//  map 1-to-1 with the MCP server's exposed tools.
//
//  Called from ClawAgentLoop.InitialiseAsync() so the plugins land in the
//  Kernel before the first agent turn.
// ============================================================================

/// <summary>
/// Loads all connected MCP servers into the Semantic Kernel as named plugins.
///
/// Each MCP server becomes a plugin named <c>Mcp_{serverName}</c>.
/// Every tool the server exposes becomes a <see cref="KernelFunction"/> inside
/// that plugin — the agent can call them exactly like built-in skills.
///
/// Example — a "filesystem" MCP server with tools read_file, write_file, list_dir:
///   Plugin "Mcp_filesystem" → KernelFunctions: read_file, write_file, list_dir
///
/// The bridge is powered by <c>Microsoft.SemanticKernel.Plugins.MCP</c>:
///   <c>IMcpClientExtensions.AsKernelPluginAsync(client, pluginName)</c>
///   converts MCP tool schemas → SK function metadata automatically.
/// </summary>
public sealed class McpKernelLoader(
    McpConnectionManager connectionManager,
    ILogger<McpKernelLoader> logger)
{
    /// <summary>
    /// Import all live MCP client tools into <paramref name="kernel"/> as plugins.
    /// Safe to call multiple times — existing Mcp_ plugins are removed and re-imported
    /// so a <c>ws reload</c> or <c>mcp_reconnect</c> takes effect immediately.
    /// </summary>
    public async Task LoadAsync(Kernel kernel, CancellationToken cancellationToken = default)
    {
        var clients = connectionManager.AllClients;

        if (clients.Count == 0)
        {
            logger.LogDebug("McpKernelLoader: No connected MCP clients — nothing to load.");
            return;
        }

        // Remove any previously loaded MCP plugins so reconnect/reload works cleanly
        var existing = kernel.Plugins
            .Where(p => p.Name.StartsWith("Mcp_", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var plugin in existing)
            kernel.Plugins.Remove(plugin);

        int loaded = 0;
        int skipped = 0;

        foreach (var (name, client) in clients)
        {
            var pluginName = $"Mcp_{SanitiseName(name)}";
            try
            {
                // Microsoft.SemanticKernel.Plugins.MCP extension:
                // Converts each MCP tool schema into a KernelFunction with the correct
                // parameter descriptions and return type, then wraps them in a KernelPlugin.
                var plugin = await client.AsKernelPluginAsync(
                    pluginName,
                    cancellationToken: cancellationToken);

                kernel.Plugins.Add(plugin);

                logger.LogInformation(
                    "McpKernelLoader: Loaded '{Plugin}' with {Count} function(s): {Funcs}",
                    pluginName,
                    plugin.FunctionCount,
                    string.Join(", ", plugin.Select(f => f.Name)));

                loaded++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "McpKernelLoader: Failed to load plugin for MCP server '{Name}'. " +
                    "Its tools will not be available to the agent.", name);
                skipped++;
            }
        }

        logger.LogInformation(
            "McpKernelLoader: {Loaded} MCP plugin(s) loaded, {Skipped} skipped.",
            loaded, skipped);
    }

    /// <summary>
    /// Reload a single server's plugin — called after <c>mcp_reconnect</c>.
    /// </summary>
    public async Task ReloadServerAsync(
        string serverName,
        Kernel kernel,
        CancellationToken cancellationToken = default)
    {
        var pluginName = $"Mcp_{SanitiseName(serverName)}";

        // Remove old plugin if present
        var old = kernel.Plugins.FirstOrDefault(
            p => p.Name.Equals(pluginName, StringComparison.OrdinalIgnoreCase));
        if (old is not null)
            kernel.Plugins.Remove(old);

        var client = connectionManager.GetClient(serverName);
        if (client is null)
        {
            logger.LogWarning(
                "McpKernelLoader: Cannot reload '{Plugin}' — client '{Server}' is not connected.",
                pluginName, serverName);
            return;
        }

        try
        {
            var plugin = await client.AsKernelPluginAsync(pluginName, cancellationToken: cancellationToken);
            kernel.Plugins.Add(plugin);
            logger.LogInformation(
                "McpKernelLoader: Reloaded '{Plugin}' with {Count} function(s).",
                pluginName, plugin.FunctionCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "McpKernelLoader: Failed to reload '{Plugin}'.", pluginName);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Sanitise a server name for use as an SK plugin name.
    /// SK plugin names must be alphanumeric + underscore only.
    /// </summary>
    private static string SanitiseName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, "[^a-zA-Z0-9_]", "_");
}
