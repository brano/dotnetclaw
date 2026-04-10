using System.Text.Json.Serialization;

namespace DotnetClaw.Gateway;

// ============================================================================
//  MessageType — well-known type discriminator constants for the wire protocol
// ============================================================================

/// <summary>
/// Well-known message-type identifiers for the WebSocket gateway wire protocol.
/// Used in both directions (client ↔ server).
/// </summary>
public static class MessageType
{
    // Server → Client
    public const string AgentChunk    = "agent_chunk";
    public const string AgentResponse = "agent_response";
    public const string ToolCall      = "tool_call";
    public const string ToolResult    = "tool_result";
    public const string Error         = "error";
    public const string ResetSession  = "reset_session";

    // Client → Server
    public const string ChatMessage   = "chat_message";
}

// ============================================================================
//  GatewayMessage — unified JSON envelope for the WebSocket wire protocol
// ============================================================================

/// <summary>
/// Unified message envelope serialised as JSON in both directions (client ↔ server).
/// Replaces SignalR's strongly-typed hub methods with an explicit wire protocol.
/// </summary>
public sealed class GatewayMessage
{
    public string Type       { get; init; } = string.Empty;
    public string? SessionId { get; init; }
    public string? Text      { get; init; }
    public string? Tool      { get; init; }
    public string? Input     { get; init; }
}

// ============================================================================
//  GatewayJsonContext — System.Text.Json source generator for AOT + perf
// ============================================================================

[JsonSerializable(typeof(GatewayMessage))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class GatewayJsonContext : JsonSerializerContext;
