using System.Net.WebSockets;
using System.Text.Json;
using DotnetClaw.Gateway;
using DotnetClaw.Gateway.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetClaw.Gateway.Tests;

public sealed class GatewayConnectionManagerTests
{
    // ── Factory helpers ───────────────────────────────────────────────────────

    private static GatewayConnectionManager CreateManager()
        => new(NullLogger<GatewayConnectionManager>.Instance);

    private static GatewayMessage MakeMessage(string type = MessageType.AgentChunk, string? session = "s1")
        => new() { Type = type, SessionId = session, Text = "hello" };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Add_RegistersConnection_InDefaultGroup()
    {
        var manager = CreateManager();
        var socket  = new FakeWebSocket(WebSocketState.Open);
        var connId  = Guid.NewGuid().ToString();
        var message = MakeMessage();

        manager.Add(connId, socket, "chan-a");
        await manager.SendAsync(connId, message);

        Assert.Single(socket.SentMessages);
        Assert.Equal(message.Type, socket.LastSentMessage()!.Type);
    }

    [Fact]
    public async Task Add_SameChannel_GroupsConnections()
    {
        var manager  = CreateManager();
        var socketA  = new FakeWebSocket(WebSocketState.Open);
        var socketB  = new FakeWebSocket(WebSocketState.Open);
        var idA      = Guid.NewGuid().ToString();
        var idB      = Guid.NewGuid().ToString();
        var message  = MakeMessage(MessageType.AgentResponse);

        manager.Add(idA, socketA, "broadcast-chan");
        manager.Add(idB, socketB, "broadcast-chan");
        await manager.SendToGroupAsync("broadcast-chan", message);

        Assert.Single(socketA.SentMessages);
        Assert.Single(socketB.SentMessages);
    }

    [Fact]
    public async Task Remove_DeregistersConnection_SoSendIsNoOp()
    {
        var manager = CreateManager();
        var socket  = new FakeWebSocket(WebSocketState.Open);
        var connId  = Guid.NewGuid().ToString();

        manager.Add(connId, socket, "chan-b");
        manager.Remove(connId);
        await manager.SendAsync(connId, MakeMessage());

        Assert.Empty(socket.SentMessages);
    }

    [Fact]
    public async Task Remove_RemovesFromGroup_ButLeavesOthers()
    {
        var manager = CreateManager();
        var sockets = Enumerable.Range(0, 3)
            .Select(_ => new FakeWebSocket(WebSocketState.Open))
            .ToList();
        var ids = sockets.Select(_ => Guid.NewGuid().ToString()).ToList();
        const string channel = "three-channel";

        for (var i = 0; i < 3; i++)
            manager.Add(ids[i], sockets[i], channel);

        // Remove the first connection
        manager.Remove(ids[0]);

        await manager.SendToGroupAsync(channel, MakeMessage(MessageType.ToolCall));

        // Removed socket must not receive anything
        Assert.Empty(sockets[0].SentMessages);

        // Remaining two must each receive exactly one message
        Assert.Single(sockets[1].SentMessages);
        Assert.Single(sockets[2].SentMessages);
    }

    [Fact]
    public async Task SendAsync_WhenSocketNotOpen_DoesNotSend()
    {
        var manager = CreateManager();
        var socket  = new FakeWebSocket(WebSocketState.Closed);
        var connId  = Guid.NewGuid().ToString();

        manager.Add(connId, socket, "chan-c");

        // Should not throw and should not call socket.SendAsync
        await manager.SendAsync(connId, MakeMessage());

        Assert.Empty(socket.SentMessages);
    }

    [Fact]
    public async Task SendAsync_UnknownConnectionId_DoesNotThrow()
    {
        var manager = CreateManager();
        var exception = await Record.ExceptionAsync(
            () => manager.SendAsync("unknown-id-12345678", MakeMessage()));

        Assert.Null(exception);
    }

    [Fact]
    public async Task SendToGroupAsync_UnknownGroup_DoesNotThrow()
    {
        var manager = CreateManager();
        var exception = await Record.ExceptionAsync(
            () => manager.SendToGroupAsync("nonexistent-group", MakeMessage()));

        Assert.Null(exception);
    }

    [Fact]
    public async Task SendToGroupAsync_SerializesCorrectJson()
    {
        var manager = CreateManager();
        var socket  = new FakeWebSocket(WebSocketState.Open);
        var connId  = Guid.NewGuid().ToString();
        var original = new GatewayMessage
        {
            Type      = MessageType.AgentChunk,
            SessionId = "session-42",
            Text      = "streaming chunk",
            Tool      = "bash",
            Input     = "ls -la"
        };

        manager.Add(connId, socket, "json-chan");
        await manager.SendToGroupAsync("json-chan", original);

        var deserialized = socket.LastSentMessage();
        Assert.NotNull(deserialized);
        Assert.Equal(original.Type,      deserialized.Type);
        Assert.Equal(original.SessionId, deserialized.SessionId);
        Assert.Equal(original.Text,      deserialized.Text);
        Assert.Equal(original.Tool,      deserialized.Tool);
        Assert.Equal(original.Input,     deserialized.Input);
    }
}
