extern alias DotnetClawCore;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DotnetClaw.E2E.Tests.Helpers;
using DotnetClawCore::DotnetClaw.Gateway;
using Xunit;

namespace DotnetClaw.E2E.Tests;

/// <summary>
/// Integration tests for the WebSocket gateway wire protocol.
/// Each test connects to a lightweight in-process echo server
/// (<see cref="GatewayTestServer"/>) that mirrors chat_message frames
/// as agent_response frames — no real agent loop required.
/// </summary>
public sealed class GatewayWebSocketIntegrationTests : IAsyncLifetime
{
    private GatewayTestServer _server = null!;

    // ── IAsyncLifetime ─────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        _server = await GatewayTestServer.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies the basic round-trip: a chat_message sent to the server comes
    /// back as an agent_response with the same text payload.
    /// </summary>
    [Fact]
    public async Task Connect_SendChatMessage_ReceivesAgentResponse()
    {
        using var ct     = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new ClientWebSocket();

        await socket.ConnectAsync(new Uri(_server.WebSocketUrl), ct.Token);

        var sent = new GatewayMessage
        {
            Type = MessageType.ChatMessage,
            Text = "Hello, gateway!",
        };

        await SendMessageAsync(socket, sent, ct.Token);

        var response = await ReceiveMessageAsync(socket, ct.Token);

        Assert.NotNull(response);
        Assert.Equal(MessageType.AgentResponse, response!.Type);
        Assert.Equal(sent.Text, response.Text);
    }

    /// <summary>
    /// Verifies that the sessionId is preserved through the server round-trip:
    /// the agent_response carries exactly the same sessionId that was sent.
    /// </summary>
    [Fact]
    public async Task Connect_WithSessionId_ResponseHasSameSessionId()
    {
        using var ct     = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new ClientWebSocket();

        await socket.ConnectAsync(new Uri(_server.WebSocketUrl), ct.Token);

        const string sessionId = "test-session-1";

        var sent = new GatewayMessage
        {
            Type      = MessageType.ChatMessage,
            SessionId = sessionId,
            Text      = "Session ID echo test",
        };

        await SendMessageAsync(socket, sent, ct.Token);

        var response = await ReceiveMessageAsync(socket, ct.Token);

        Assert.NotNull(response);
        Assert.Equal(sessionId, response!.SessionId);
    }

    /// <summary>
    /// Verifies that sending multiple chat messages sequentially results in a
    /// corresponding agent_response for each one, with the correct text payload.
    /// </summary>
    [Fact]
    public async Task SendMultipleMessages_AllReceiveResponses()
    {
        using var ct     = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new ClientWebSocket();

        await socket.ConnectAsync(new Uri(_server.WebSocketUrl), ct.Token);

        var messages = new[]
        {
            "First message",
            "Second message",
            "Third message",
        };

        foreach (var text in messages)
        {
            var sent = new GatewayMessage
            {
                Type = MessageType.ChatMessage,
                Text = text,
            };

            await SendMessageAsync(socket, sent, ct.Token);

            var response = await ReceiveMessageAsync(socket, ct.Token);

            Assert.NotNull(response);
            Assert.Equal(MessageType.AgentResponse, response!.Type);
            Assert.Equal(text, response.Text);
        }
    }

    /// <summary>
    /// Verifies that the server accepts connections that include a ?channel=
    /// query parameter; the WebSocket handshake completes and the connection
    /// is usable for a normal message exchange.
    /// </summary>
    [Fact]
    public async Task ConnectWithChannel_QueryParam_Accepted()
    {
        using var ct     = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new ClientWebSocket();

        // Append the channel query parameter exactly as the real gateway expects
        var urlWithChannel = _server.WebSocketUrl + "?channel=test-channel";
        await socket.ConnectAsync(new Uri(urlWithChannel), ct.Token);

        // Connection accepted when the socket reaches Open state
        Assert.Equal(WebSocketState.Open, socket.State);

        // Confirm a round-trip still works over the parameterised connection
        var sent = new GatewayMessage
        {
            Type = MessageType.ChatMessage,
            Text = "Channel param test",
        };

        await SendMessageAsync(socket, sent, ct.Token);

        var response = await ReceiveMessageAsync(socket, ct.Token);

        Assert.NotNull(response);
        Assert.Equal(MessageType.AgentResponse, response!.Type);
    }

    /// <summary>
    /// Pure unit test (lives here for convenience): verifies that
    /// <see cref="GatewayMessage"/> serialises to JSON via
    /// <see cref="GatewayJsonContext"/> and deserialises back with all
    /// fields intact and matching.
    /// </summary>
    [Fact]
    public void GatewayMessage_Serialization_RoundTrip()
    {
        var original = new GatewayMessage
        {
            Type      = MessageType.ChatMessage,
            SessionId = "session-abc",
            Text      = "Serialisation test payload",
            Tool      = "some_tool",
            Input     = "{\"key\":\"value\"}",
        };

        var bytes        = JsonSerializer.SerializeToUtf8Bytes(original, GatewayJsonContext.Default.GatewayMessage);
        var deserialized = JsonSerializer.Deserialize(bytes, GatewayJsonContext.Default.GatewayMessage);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Type,      deserialized!.Type);
        Assert.Equal(original.SessionId, deserialized.SessionId);
        Assert.Equal(original.Text,      deserialized.Text);
        Assert.Equal(original.Tool,      deserialized.Tool);
        Assert.Equal(original.Input,     deserialized.Input);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a complete WebSocket message (potentially spanning multiple frames)
    /// and deserialises it as a <see cref="GatewayMessage"/>.
    /// Returns <c>null</c> when the server sends a Close frame.
    /// </summary>
    private static async Task<GatewayMessage?> ReceiveMessageAsync(
        ClientWebSocket socket,
        CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        var json = Encoding.UTF8.GetString(ms.ToArray());
        return JsonSerializer.Deserialize(json, GatewayJsonContext.Default.GatewayMessage);
    }

    /// <summary>
    /// Serialises <paramref name="msg"/> and sends it to <paramref name="socket"/>
    /// as a single text frame.
    /// </summary>
    private static async Task SendMessageAsync(
        ClientWebSocket socket,
        GatewayMessage msg,
        CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg, GatewayJsonContext.Default.GatewayMessage);
        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken: ct);
    }
}
