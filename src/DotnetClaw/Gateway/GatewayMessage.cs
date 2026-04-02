namespace DotnetClaw.Gateway;

// ============================================================================
//  MessageType — well-known type discriminator constants
//  (used by WebGatewayClientService for subscriber routing)
// ============================================================================

/// <summary>
/// Well-known message-type identifiers shared between the server and the
/// <c>WebGatewayClientService</c> subscriber routing logic.
/// </summary>
public static class MessageType
{
    public const string AgentChunk     = "agent_chunk";
    public const string AgentResponse  = "agent_response";
    public const string ToolCall       = "tool_call";
    public const string ToolResult     = "tool_result";
    public const string Error          = "error";
    public const string ResetSession   = "reset_session";
}

// ============================================================================
//  GatewayMessage — internal envelope used by WebGatewayClientService
//  to route SignalR callbacks to per-session Blazor subscribers
// ============================================================================

/// <summary>
/// Internal event envelope passed from <c>WebGatewayClientService</c> to
/// per-session subscribers (AgentBridgeService, TerminalService).
/// Not serialised over the wire — SignalR handles that via strongly-typed methods.
/// </summary>
public sealed class GatewayMessage
{
    public string Type       { get; init; } = string.Empty;
    public string? SessionId { get; init; }
    public string? Text      { get; init; }
    public string? Tool      { get; init; }
    public string? Input     { get; init; }
}
