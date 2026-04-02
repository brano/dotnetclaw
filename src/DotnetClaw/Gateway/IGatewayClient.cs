namespace DotnetClaw.Gateway;

// ============================================================================
//  IGatewayClient — typed SignalR client interface (server → client methods)
// ============================================================================

/// <summary>
/// Defines the methods the server can invoke on connected gateway clients.
/// Used as the type parameter for <see cref="GatewayHub"/> so the compiler
/// enforces the outbound contract.
/// </summary>
public interface IGatewayClient
{
    /// <summary>Streaming token chunk from the agent.</summary>
    Task ReceiveChunk(string sessionId, string text);

    /// <summary>Full agent response — end of turn.</summary>
    Task ReceiveAgentResponse(string sessionId, string text);

    /// <summary>Agent is invoking a tool.</summary>
    Task ReceiveToolCall(string sessionId, string tool, string input);

    /// <summary>Tool execution result.</summary>
    Task ReceiveToolResult(string sessionId, string tool, string result);

    /// <summary>An error occurred during the agent turn.</summary>
    Task ReceiveError(string sessionId, string message);

    /// <summary>Confirmation that the session was reset.</summary>
    Task OnSessionReset(string sessionId);
}
