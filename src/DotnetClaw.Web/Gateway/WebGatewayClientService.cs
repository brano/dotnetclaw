using System.Collections.Concurrent;
using DotnetClaw.Gateway;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace DotnetClaw.Web.Gateway;

// ============================================================================
//  WebGatewayClientService — singleton SignalR client for the CLI gateway
// ============================================================================

/// <summary>
/// Singleton hosted service that maintains a SignalR connection to the DotnetClaw
/// CLI gateway (ws://localhost:5050/ws by default).
///
/// Architecture:
///   • One shared <see cref="HubConnection"/> for the entire Blazor server process.
///   • Per-session subscribers: Blazor components subscribe with a unique sessionId
///     and receive only messages whose sessionId matches theirs.
///   • <see cref="HubConnectionBuilder.WithAutomaticReconnect"/> handles reconnection.
/// </summary>
public sealed class WebGatewayClientService : IHostedService, IAsyncDisposable
{
    private readonly GatewayClientOptions _options;
    private readonly ILogger<WebGatewayClientService> _logger;

    private HubConnection? _connection;

    // sessionId → subscriber callback
    private readonly ConcurrentDictionary<string, Func<GatewayMessage, Task>> _subscribers = new();

    /// <summary>Whether the SignalR connection is currently active.</summary>
    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public WebGatewayClientService(
        IOptions<GatewayClientOptions> options,
        ILogger<WebGatewayClientService> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var url = $"{_options.ServerUrl}?channel={_options.Channel}";

        _connection = new HubConnectionBuilder()
            .WithUrl(url)
            .WithAutomaticReconnect()
            .Build();

        RegisterHandlers(_connection);

        _connection.Reconnecting += ex =>
        {
            _logger.LogWarning("WebGatewayClient: reconnecting… ({Reason})", ex?.Message);
            return Task.CompletedTask;
        };
        _connection.Reconnected += _ =>
        {
            _logger.LogInformation("WebGatewayClient: reconnected");
            return Task.CompletedTask;
        };
        _connection.Closed += ex =>
        {
            _logger.LogWarning("WebGatewayClient: connection closed ({Reason})", ex?.Message);
            return Task.CompletedTask;
        };

        await ConnectWithRetryAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
            await _connection.StopAsync(cancellationToken);
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
        => InvokeAsync("SendChatMessage", ct, sessionId, text);

    /// <summary>Asks the CLI to reset the agent conversation for <paramref name="sessionId"/>.</summary>
    public Task SendResetAsync(string sessionId, CancellationToken ct = default)
        => InvokeAsync("ResetSession", ct, sessionId);

    // ── Internal ──────────────────────────────────────────────────────────────

    private void RegisterHandlers(HubConnection conn)
    {
        conn.On<string, string>("ReceiveChunk", (sessionId, text) =>
            Dispatch(new GatewayMessage { Type = MessageType.AgentChunk, SessionId = sessionId, Text = text }));

        conn.On<string, string>("ReceiveAgentResponse", (sessionId, text) =>
            Dispatch(new GatewayMessage { Type = MessageType.AgentResponse, SessionId = sessionId, Text = text }));

        conn.On<string, string, string>("ReceiveToolCall", (sessionId, tool, input) =>
            Dispatch(new GatewayMessage { Type = MessageType.ToolCall, SessionId = sessionId, Tool = tool, Input = input }));

        conn.On<string, string, string>("ReceiveToolResult", (sessionId, tool, result) =>
            Dispatch(new GatewayMessage { Type = MessageType.ToolResult, SessionId = sessionId, Tool = tool, Text = result }));

        conn.On<string, string>("ReceiveError", (sessionId, message) =>
            Dispatch(new GatewayMessage { Type = MessageType.Error, SessionId = sessionId, Text = message }));

        conn.On<string>("OnSessionReset", sessionId =>
            Dispatch(new GatewayMessage { Type = MessageType.ResetSession, SessionId = sessionId }));
    }

    private Task Dispatch(GatewayMessage msg)
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

    private async Task InvokeAsync(string method, CancellationToken ct, params object[] args)
    {
        if (_connection?.State != HubConnectionState.Connected)
        {
            _logger.LogWarning("WebGatewayClient: cannot invoke '{Method}' — not connected", method);
            return;
        }
        try
        {
            await _connection.InvokeCoreAsync(method, args, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebGatewayClient: invoke '{Method}' failed", method);
        }
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _connection!.StartAsync(ct);
                _logger.LogInformation("WebGatewayClient: connected to {Url}", _options.ServerUrl);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex,
                    "WebGatewayClient: initial connect failed, retrying in {Delay}s",
                    _options.ReconnectDelaySeconds);
                try { await Task.Delay(TimeSpan.FromSeconds(_options.ReconnectDelaySeconds), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
