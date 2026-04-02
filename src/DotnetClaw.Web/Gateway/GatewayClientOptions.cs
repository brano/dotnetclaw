namespace DotnetClaw.Web.Gateway;

// ============================================================================
//  GatewayClientOptions — configuration for the Web-side WebSocket client
// ============================================================================

/// <summary>
/// Configuration for the Blazor server's outbound connection to the DotnetClaw
/// WebSocket Gateway hosted by the CLI process.
/// Bound from <c>appsettings.json</c> under the <c>"GatewayClient"</c> section.
/// </summary>
public sealed class GatewayClientOptions
{
    public const string SectionName = "GatewayClient";

    /// <summary>Full WebSocket URL of the CLI gateway (e.g. <c>ws://localhost:5050/ws</c>).</summary>
    public string ServerUrl { get; set; } = "ws://localhost:5050/ws";

    /// <summary>Channel to claim in the <c>hello</c> handshake.</summary>
    public string Channel { get; set; } = "web-ui";

    /// <summary>Seconds to wait before re-attempting a dropped connection.</summary>
    public int ReconnectDelaySeconds { get; set; } = 5;
}
