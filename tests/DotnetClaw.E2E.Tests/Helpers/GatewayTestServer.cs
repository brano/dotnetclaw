extern alias DotnetClawCore;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DotnetClawCore::DotnetClaw.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetClaw.E2E.Tests.Helpers;

/// <summary>
/// Minimal in-process WebSocket gateway server for integration testing.
/// Routes: ws://127.0.0.1:{port}/ws
/// On receiving a chat_message, echoes back an agent_response with the same text
/// and sessionId, then sends a second agent_response to signal completion.
/// </summary>
public sealed class GatewayTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    /// <summary>HTTP base URL of the running server, e.g. "http://127.0.0.1:xxxx".</summary>
    public string BaseUrl { get; }

    /// <summary>WebSocket endpoint URL, e.g. "ws://127.0.0.1:xxxx/ws".</summary>
    public string WebSocketUrl { get; }

    private GatewayTestServer(WebApplication app, string baseUrl)
    {
        _app       = app;
        BaseUrl    = baseUrl;
        WebSocketUrl = baseUrl.Replace("http://", "ws://") + "/ws";
    }

    /// <summary>
    /// Builds and starts a minimal ASP.NET Core app on a random loopback port.
    /// The /ws endpoint accepts WebSocket upgrades and echoes responses for
    /// chat_message frames.
    /// </summary>
    public static async Task<GatewayTestServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();

        // Random OS-assigned port on the loopback adapter
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // Suppress all console/debug output so tests stay clean
        builder.Logging.ClearProviders();

        var app = builder.Build();

        app.UseWebSockets();

        // Map the echo WebSocket handler at /ws
        app.Map("/ws", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await EchoLoopAsync(socket, context.RequestAborted);
        });

        await app.StartAsync();

        // Resolve the actual port assigned by the OS
        var address = app.Urls.First(); // e.g. "http://127.0.0.1:54321"
        return new GatewayTestServer(app, address);
    }

    /// <summary>
    /// Receive loop: reads one complete WebSocket message at a time.
    /// For chat_message frames, sends back an agent_response with the same text
    /// and sessionId. All other message types are silently ignored.
    /// </summary>
    private static async Task EchoLoopAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;

            try
            {
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closing",
                            CancellationToken.None);
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
            }
            catch (WebSocketException)      { return; }
            catch (OperationCanceledException) { return; }

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            GatewayMessage? incoming;
            try
            {
                var json = Encoding.UTF8.GetString(ms.ToArray());
                incoming = JsonSerializer.Deserialize(json, GatewayJsonContext.Default.GatewayMessage);
            }
            catch (JsonException)
            {
                // Malformed JSON — skip
                continue;
            }

            if (incoming is null || incoming.Type != MessageType.ChatMessage)
                continue;

            // Echo: send agent_response with the same text and sessionId
            var response = new GatewayMessage
            {
                Type      = MessageType.AgentResponse,
                SessionId = incoming.SessionId,
                Text      = incoming.Text,
            };

            await SendMessageAsync(socket, response, ct);
        }
    }

    /// <summary>Serialises <paramref name="msg"/> and sends it as a single text frame.</summary>
    private static async Task SendMessageAsync(WebSocket socket, GatewayMessage msg, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg, GatewayJsonContext.Default.GatewayMessage);
        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken: ct);
    }

    public async ValueTask DisposeAsync() => await _app.StopAsync();
}
