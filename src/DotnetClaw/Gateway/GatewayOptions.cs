namespace DotnetClaw.Gateway;

// ============================================================================
//  GatewayOptions — configuration for the ASP.NET Core WebSocket Gateway
// ============================================================================

/// <summary>
/// Configuration options for the ASP.NET Core WebSocket Gateway.
/// Bound from <c>appsettings.json</c> under the <c>"Gateway"</c> section.
/// </summary>
public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>Master switch — when false the /ws hub is not mapped.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>URL path for the WebSocket gateway endpoint.</summary>
    public string Path { get; set; } = "/ws";

    /// <summary>TCP port on which Kestrel listens for gateway connections.</summary>
    public int Port { get; set; } = 5050;

    /// <summary>Channel names accepted by the gateway (validated on connect).</summary>
    public string[] AllowedChannels { get; set; } = ["web-ui", "cli", "telegram"];

    /// <summary>Maximum number of simultaneous connections.</summary>
    public int MaxConnections { get; set; } = 50;
}
