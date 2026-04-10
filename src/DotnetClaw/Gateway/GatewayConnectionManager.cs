using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DotnetClaw.Gateway;

// ============================================================================
//  GatewayConnectionManager — WebSocket connection registry + group broadcast
// ============================================================================

/// <summary>
/// Manages active WebSocket connections and provides group-based broadcasting.
/// Registered as a singleton — replaces the role of <c>IHubContext</c> from SignalR.
/// </summary>
public sealed class GatewayConnectionManager(ILogger<GatewayConnectionManager> logger)
{
    private readonly ConcurrentDictionary<string, GatewayConnection> _connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _groups = new();

    /// <summary>Registers a new WebSocket connection and adds it to the specified channel group.</summary>
    public void Add(string connectionId, WebSocket socket, string channel)
    {
        var conn = new GatewayConnection(connectionId, socket, channel, new SemaphoreSlim(1, 1));
        _connections[connectionId] = conn;

        var group = _groups.GetOrAdd(channel, _ => new ConcurrentDictionary<string, byte>());
        group[connectionId] = 0;

        logger.LogInformation(
            "Gateway: {Id} connected on channel '{Channel}'",
            connectionId[..8], channel);
    }

    /// <summary>Removes a connection from the registry and its channel group.</summary>
    public void Remove(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var conn))
        {
            if (_groups.TryGetValue(conn.Channel, out var group))
                group.TryRemove(connectionId, out _);

            conn.SendLock.Dispose();
            logger.LogInformation("Gateway: {Id} disconnected", connectionId[..8]);
        }
    }

    /// <summary>Sends a message to a specific connection.</summary>
    public async Task SendAsync(string connectionId, GatewayMessage message, CancellationToken ct = default)
    {
        if (_connections.TryGetValue(connectionId, out var conn))
            await SendCoreAsync(conn, message, ct);
    }

    /// <summary>Sends a message to all connections in a channel group.</summary>
    public async Task SendToGroupAsync(string group, GatewayMessage message, CancellationToken ct = default)
    {
        if (!_groups.TryGetValue(group, out var members))
            return;

        var tasks = new List<Task>();
        foreach (var connId in members.Keys)
        {
            if (_connections.TryGetValue(connId, out var conn))
                tasks.Add(SendCoreAsync(conn, message, ct));
        }
        await Task.WhenAll(tasks);
    }

    private async Task SendCoreAsync(GatewayConnection conn, GatewayMessage message, CancellationToken ct)
    {
        if (conn.Socket.State != WebSocketState.Open)
            return;

        var json = JsonSerializer.SerializeToUtf8Bytes(message, GatewayJsonContext.Default.GatewayMessage);

        await conn.SendLock.WaitAsync(ct);
        try
        {
            await conn.Socket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (WebSocketException ex)
        {
            logger.LogWarning(ex, "Gateway: failed to send to {Id}", conn.Id[..8]);
        }
        finally
        {
            conn.SendLock.Release();
        }
    }
}

/// <summary>Represents a single active WebSocket connection.</summary>
internal sealed record GatewayConnection(
    string Id,
    WebSocket Socket,
    string Channel,
    SemaphoreSlim SendLock);
