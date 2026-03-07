using System.ComponentModel;
using System.Text;
using System.Text.Json;
using DotnetClaw.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotnetClaw.Plugins;

/// <summary>
/// MCP management skill — lets the agent introspect and directly interact with
/// connected MCP servers at the protocol level.
///
/// The actual MCP <em>tools</em> from each server are imported as separate SK plugins
/// named <c>Mcp_{serverName}</c> by <c>McpKernelLoader</c> — the agent calls those
/// directly by their natural function names.
///
/// This plugin handles the management plane:
///   • List servers and their connection status
///   • List tools available on a specific server
///   • Call a tool by name with raw JSON arguments (useful for debugging)
///   • Reconnect a failed server
///   • List and read MCP resources (files, database rows, etc.)
/// </summary>
public sealed class McpPlugin(
    DotnetClaw.Mcp.McpConnectionManager connectionManager,
    DotnetClaw.Mcp.McpKernelLoader kernelLoader,
    IOptions<McpOptions> mcpOptions,
    ILogger<McpPlugin> logger)
{
    private readonly McpOptions _options = mcpOptions.Value;

    // =========================================================================
    // Server status
    // =========================================================================

    [KernelFunction("mcp_list_servers")]
    [Description(
        "List all configured MCP servers, their connection status, and how many tools each exposes. " +
        "Use this to understand what MCP capabilities are available before calling mcp_list_tools.")]
    public string ListServers()
    {
        var statuses = connectionManager.GetStatuses().ToList();

        if (statuses.Count == 0)
            return "No MCP servers are configured. " +
                   "Add servers under DotnetClaw:Mcp:Servers in appsettings.json.";

        var sb = new StringBuilder();
        sb.AppendLine($"MCP Servers ({statuses.Count} configured):");
        sb.AppendLine();

        foreach (var s in statuses.OrderBy(x => x.Name))
        {
            var icon = s.Connected ? "✅" : "❌";
            sb.AppendLine($"{icon} {s.Name} [{s.Transport}]");
            if (!string.IsNullOrWhiteSpace(s.Description))
                sb.AppendLine($"   {s.Description}");
            if (s.Connected)
                sb.AppendLine($"   {s.ToolCount} tool(s) — connected at {s.ConnectedAt:HH:mm:ss}");
            else
                sb.AppendLine($"   Error: {s.Error}");
        }

        return sb.ToString().TrimEnd();
    }

    // =========================================================================
    // Tool discovery
    // =========================================================================

    [KernelFunction("mcp_list_tools")]
    [Description(
        "List all tools available on a specific MCP server, including each tool's name, " +
        "description, and input parameters. " +
        "Use this to understand what a server can do before calling its tools.")]
    public async Task<string> ListToolsAsync(
        [Description("Name of the MCP server. Use mcp_list_servers to see available names.")]
        string serverName,

        CancellationToken cancellationToken = default)
    {
        var client = connectionManager.GetClient(serverName);
        if (client is null)
            return $"[ERROR] Server '{serverName}' is not connected. " +
                   $"Use mcp_list_servers to see available servers, or mcp_reconnect to retry.";

        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);

            if (tools.Count == 0)
                return $"Server '{serverName}' has no tools.";

            var sb = new StringBuilder();
            sb.AppendLine($"Tools on '{serverName}' ({tools.Count}):");
            sb.AppendLine();

            foreach (var tool in tools.OrderBy(t => t.Name))
            {
                sb.AppendLine($"  {tool.Name}");
                if (!string.IsNullOrWhiteSpace(tool.Description))
                    sb.AppendLine($"    {tool.Description}");

                // Surface required input parameters from the JSON Schema
                var schema = tool.ProtocolTool.InputSchema;
                if (schema.ValueKind == JsonValueKind.Object
                    && schema.TryGetProperty("properties", out var propsEl)
                    && propsEl.ValueKind == JsonValueKind.Object)
                {
                    var requiredSet = schema.TryGetProperty("required", out var reqEl)
                        ? reqEl.EnumerateArray().Select(e => e.GetString()).ToHashSet()
                        : [];

                    foreach (var prop in propsEl.EnumerateObject())
                    {
                        var req = requiredSet.Contains(prop.Name) ? " (required)" : " (optional)";
                        var desc = "";
                        if (prop.Value.TryGetProperty("description", out var descEl))
                            desc = descEl.GetString() ?? "";
                        else if (prop.Value.TryGetProperty("type", out var typeEl))
                            desc = typeEl.GetString() ?? "";
                        sb.AppendLine($"    • {prop.Name}{req}: {desc}");
                    }
                }

                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "mcp_list_tools failed for server '{Server}'", serverName);
            return $"[ERROR] Failed to list tools from '{serverName}': {ex.Message}";
        }
    }

    // =========================================================================
    // Raw tool call (debug / fallback)
    // =========================================================================

    [KernelFunction("mcp_call_tool")]
    [Description(
        "Call a specific tool on an MCP server with raw JSON arguments and return the result. " +
        "The MCP tools are also available as direct SK functions under Mcp_{serverName}.{toolName} — " +
        "prefer those for normal use. Use mcp_call_tool for debugging, exploring, or when the " +
        "auto-imported plugin is unavailable.")]
    public async Task<string> CallToolAsync(
        [Description("Name of the MCP server. Example: 'filesystem'")]
        string serverName,

        [Description("Exact name of the tool to call. Use mcp_list_tools to discover names.")]
        string toolName,

        [Description(
            "Tool arguments as a JSON object string. " +
            "Example: '{\"path\": \"/home/user/file.txt\"}'. " +
            "Pass '{}' for tools that take no arguments.")]
        string argumentsJson = "{}",

        CancellationToken cancellationToken = default)
    {
        var client = connectionManager.GetClient(serverName);
        if (client is null)
            return $"[ERROR] Server '{serverName}' is not connected.";

        // Deserialize as JsonElement values, then box to object? for the new API
        Dictionary<string, JsonElement>? rawArgs = null;
        try
        {
            rawArgs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson);
        }
        catch (JsonException ex)
        {
            return $"[ERROR] Invalid JSON arguments: {ex.Message}\nProvided: {argumentsJson}";
        }

        // Convert to IReadOnlyDictionary<string, object?> required by v1.x API
        IReadOnlyDictionary<string, object?>? arguments =
            rawArgs?.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);

        if (_options.LogToolCallDetails)
            logger.LogDebug("MCP call: {Server}.{Tool}({Args})", serverName, toolName, argumentsJson);

        try
        {
            var result = await client.CallToolAsync(
                toolName,
                arguments,
                cancellationToken: cancellationToken);

            // Flatten the content list into a readable string using pattern matching on v1.x types
            var sb = new StringBuilder();
            foreach (var content in result.Content)
            {
                switch (content)
                {
                    case TextContentBlock tc:
                        sb.AppendLine(tc.Text);
                        break;
                    case ImageContentBlock ic:
                        sb.AppendLine($"[Image: {ic.MimeType}, {ic.Data.Length} bytes]");
                        break;
                    default:
                        sb.AppendLine($"[{content.Type}]");
                        break;
                }
            }

            var output = sb.ToString().TrimEnd();

            if (_options.LogToolCallDetails)
                logger.LogDebug("MCP result: {Server}.{Tool} → {Result}", serverName, toolName, output[..Math.Min(200, output.Length)]);

            return string.IsNullOrWhiteSpace(output) ? "(empty result)" : output;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "mcp_call_tool failed: {Server}.{Tool}", serverName, toolName);
            return $"[ERROR] Tool call failed: {ex.Message}";
        }
    }

    // =========================================================================
    // Reconnect
    // =========================================================================

    [KernelFunction("mcp_reconnect")]
    [Description(
        "Disconnect and reconnect a specific MCP server, then reload its tools into the agent. " +
        "Use this when a server has crashed, timed out, or you've updated its configuration.")]
    public async Task<string> ReconnectAsync(
        [Description("Name of the MCP server to reconnect.")]
        string serverName,

        [Description("The active SK Kernel — required to reload the plugin after reconnect.")]
        Kernel kernel,

        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Reconnecting MCP server '{Name}'…", serverName);

        var status = await connectionManager.ReconnectAsync(serverName, cancellationToken);

        if (!status.Connected)
            return $"[ERROR] Reconnect failed for '{serverName}': {status.Error}";

        // Re-import the server's tools into the kernel
        await kernelLoader.ReloadServerAsync(serverName, kernel, cancellationToken);

        return $"[OK] '{serverName}' reconnected — {status.ToolCount} tool(s) reloaded.";
    }

    // =========================================================================
    // Resources
    // =========================================================================

    [KernelFunction("mcp_list_resources")]
    [Description(
        "List the resources exposed by an MCP server. " +
        "Resources are named data items (files, database rows, API data) that can be read via mcp_read_resource. " +
        "Not all servers expose resources — check mcp_list_servers first.")]
    public async Task<string> ListResourcesAsync(
        [Description("Name of the MCP server.")]
        string serverName,

        CancellationToken cancellationToken = default)
    {
        var client = connectionManager.GetClient(serverName);
        if (client is null)
            return $"[ERROR] Server '{serverName}' is not connected.";

        try
        {
            var resources = await client.ListResourcesAsync(cancellationToken: cancellationToken);

            if (resources.Count == 0)
                return $"Server '{serverName}' exposes no resources.";

            var sb = new StringBuilder();
            sb.AppendLine($"Resources on '{serverName}' ({resources.Count}):");
            sb.AppendLine();

            foreach (var r in resources)
            {
                sb.AppendLine($"  {r.Name}");
                sb.AppendLine($"    URI:  {r.Uri}");
                if (!string.IsNullOrWhiteSpace(r.Description))
                    sb.AppendLine($"    {r.Description}");
                if (!string.IsNullOrWhiteSpace(r.MimeType))
                    sb.AppendLine($"    Type: {r.MimeType}");
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "mcp_list_resources failed for server '{Server}'", serverName);
            return $"[ERROR] Failed to list resources from '{serverName}': {ex.Message}";
        }
    }

    [KernelFunction("mcp_read_resource")]
    [Description(
        "Read the content of a resource from an MCP server by its URI. " +
        "Use mcp_list_resources to discover available resource URIs. " +
        "Returns the resource content as text.")]
    public async Task<string> ReadResourceAsync(
        [Description("Name of the MCP server.")]
        string serverName,

        [Description(
            "URI of the resource to read. " +
            "Example: 'file:///home/user/notes.md' or 'db://mydb/users/42'")]
        string resourceUri,

        CancellationToken cancellationToken = default)
    {
        var client = connectionManager.GetClient(serverName);
        if (client is null)
            return $"[ERROR] Server '{serverName}' is not connected.";

        try
        {
            var result = await client.ReadResourceAsync(resourceUri, cancellationToken: cancellationToken);

            var sb = new StringBuilder();
            foreach (var content in result.Contents)
            {
                switch (content)
                {
                    case TextResourceContents tc when !string.IsNullOrWhiteSpace(tc.Text):
                        sb.AppendLine(tc.Text);
                        break;
                    case BlobResourceContents bc when bc.Blob.Length > 0:
                        sb.AppendLine($"[Binary resource, {bc.Blob.Length} bytes]");
                        break;
                }
            }

            var text = sb.ToString().TrimEnd();
            return string.IsNullOrWhiteSpace(text) ? "(empty resource)" : text;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "mcp_read_resource failed: {Server} {Uri}", serverName, resourceUri);
            return $"[ERROR] Failed to read resource '{resourceUri}' from '{serverName}': {ex.Message}";
        }
    }
}
