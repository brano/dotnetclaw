using System.Net.WebSockets;
using System.Text.Json;
using DotnetClaw.Gateway;

namespace DotnetClaw.Gateway.Tests.Helpers;

/// <summary>
/// A concrete, in-memory <see cref="WebSocket"/> substitute for unit tests.
/// All <see cref="SendAsync"/> calls are captured in <see cref="SentMessages"/>
/// in a thread-safe manner. All other abstract members are implemented as
/// no-ops or return sensible defaults.
/// </summary>
public sealed class FakeWebSocket : WebSocket
{
    private WebSocketState _state;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>All raw byte payloads received via <see cref="SendAsync"/>.</summary>
    public List<byte[]> SentMessages { get; } = new();

    public FakeWebSocket(WebSocketState state = WebSocketState.Open)
    {
        _state = state;
    }

    // ── WebSocket state ───────────────────────────────────────────────────────

    public override WebSocketState State => _state;

    /// <summary>Allows tests to transition the socket state after construction.</summary>
    public void SetState(WebSocketState state) => _state = state;

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;

    // ── SendAsync — captures payloads ─────────────────────────────────────────

    public override async Task SendAsync(
        ArraySegment<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var copy = buffer.Array is not null
            ? buffer.ToArray()
            : Array.Empty<byte>();

        await _lock.WaitAsync(cancellationToken);
        try
        {
            SentMessages.Add(copy);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── ReceiveAsync — returns a Close frame so callers don't hang ────────────

    public override Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new WebSocketReceiveResult(
            count: 0,
            messageType: WebSocketMessageType.Close,
            endOfMessage: true,
            closeStatus: WebSocketCloseStatus.NormalClosure,
            closeStatusDescription: "fake"));
    }

    // ── CloseAsync / CloseOutputAsync ─────────────────────────────────────────

    public override Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
    {
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
    {
        _state = WebSocketState.CloseSent;
        return Task.CompletedTask;
    }

    // ── Abort / Dispose ───────────────────────────────────────────────────────

    public override void Abort()
    {
        _state = WebSocketState.Aborted;
    }

    public override void Dispose()
    {
        _state = WebSocketState.Closed;
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deserialises the last captured message back to a <see cref="GatewayMessage"/>
    /// using the source-generated <see cref="GatewayJsonContext"/>.
    /// Returns <c>null</c> when no messages have been sent yet.
    /// </summary>
    public GatewayMessage? LastSentMessage()
    {
        if (SentMessages.Count == 0)
            return null;

        var bytes = SentMessages[SentMessages.Count - 1];
        return JsonSerializer.Deserialize(bytes, GatewayJsonContext.Default.GatewayMessage);
    }
}
