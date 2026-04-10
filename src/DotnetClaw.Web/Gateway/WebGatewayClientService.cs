using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DotnetClaw.Gateway;
using Microsoft.Extensions.Options;

namespace DotnetClaw.Web.Gateway;

// ============================================================================
//  WebGatewayClientService — singleton WebSocket client for the CLI gateway
// ============================================================================

/// <summary>
/// Singleton hosted service that maintains a raw WebSocket connection to the
/// DotnetClaw CLI gateway (ws://localhost:5050/ws by default).
///
/// Architecture:
///   • One shared <see cref="ClientWebSocket"/> for the entire Blazor server process.
///   • Per-session subscribers: Blazor components subscribe with a unique sessionId
///     and receive only messages whose sessionId matches theirs.
///   • Automatic reconnection with configurable delay on connection loss.
/// </summary>
public sealed class WebGatewayClientService : IHostedService, IAsyncDisposable
{
    private readonly GatewayClientOptions _options;
    private readonly ILogger<WebGatewayClientService> _logger;

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    // sessionId → subscriber callback
    private readonly ConcurrentDictionary<string, Func<GatewayMessage, Task>> _subscribers = new();

    /// <summary>Whether the WebSocket connection is currently active.</summary>
    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public WebGatewayClientService(
        IOptions<GatewayClientOptions> options,
        ILogger<WebGatewayClientService> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _runTask = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();

        if (_socket?.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "Stopping", cancellationToken);
            }
            catch { /* best effort */ }
        }

        if (_runTask is not null)
        {
            try { await _runTask; }
            catch { /* swallow cancellation */ }
        }
    }

    // ── Subscription API ─────────────────────────────────────────────────────

    /// <summary>
    /// Registers <paramref name="handler"/> to receive all gateway messages whose
    /// sessionId matches <paramref name="sessionId"/>.
    /// </summary>
    public void Subscribe(string sessionId, Func<GatewayMessage, Task> handler)
        => _subscribers[sessionId] = handler;

    /// <summary>Removes the subscriber for <paramref name="sessionId"/>.</summary>
    public void Unsubscribe(string sessionId)
        => _subscribers.TryRemove(sessionId, out _);

    // ── Send API ──────────────────────────────────────────────────────────────

    /// <summary>Sends a chat message to the gateway for the given session.</summary>
    public Task SendChatMessageAsync(string text, string sessionId, CancellationToken ct = default)
        => SendAsync(new GatewayMessage
        {
            Type = MessageType.ChatMessage, SessionId = sessionId, Text = text
        }, ct);

    /// <summary>Asks the CLI to reset the agent conversation for <paramref name="sessionId"/>.</summary>
    public Task SendResetAsync(string sessionId, CancellationToken ct = default)
        => SendAsync(new GatewayMessage
        {
            Type = MessageType.ResetSession, SessionId = sessionId
        }, ct);

    // ── Internal ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Main loop: connect → receive → reconnect. Runs in the background until cancelled.
    /// </summary>
    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _socket?.Dispose();
                _socket = new ClientWebSocket();

                var url = $"{_options.ServerUrl}?channel={_options.Channel}";
                await _socket.ConnectAsync(new Uri(url), ct);
                _logger.LogInformation("WebGatewayClient: connected to {Url}", _options.ServerUrl);

                await ReceiveLoopAsync(ct);

                if (!ct.IsCancellationRequested)
                    _logger.LogWarning("WebGatewayClient: connection lost, reconnecting…");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex,
                    "WebGatewayClient: connect failed, retrying in {Delay}s",
                    _options.ReconnectDelaySeconds);
            }

            if (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(_options.ReconnectDelaySeconds), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (_socket?.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            try
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(ms.ToArray());
                var message = JsonSerializer.Deserialize(json, GatewayJsonContext.Default.GatewayMessage);

                if (message is not null)
                    await DispatchAsync(message);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WebGatewayClient: receive error");
                break;
            }
        }
    }

    private Task DispatchAsync(GatewayMessage msg)
    {
        if (msg.SessionId is not null && _subscribers.TryGetValue(msg.SessionId, out var handler))
        {
            try { return handler(msg); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WebGatewayClient: subscriber threw for session {Session}", msg.SessionId);
            }
        }
        return Task.CompletedTask;
    }

    private async Task SendAsync(GatewayMessage message, CancellationToken ct)
    {
        if (_socket?.State != WebSocketState.Open)
        {
            _logger.LogWarning("WebGatewayClient: cannot send — not connected");
            return;
        }
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(message, GatewayJsonContext.Default.GatewayMessage);
            await _socket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebGatewayClient: send failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _socket?.Dispose();
    }
}
