using System.Text.Json;
using DotnetClaw.Gateway;
using Xunit;

namespace DotnetClaw.Gateway.Tests;

public sealed class GatewayMessageSerializationTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Serialize(GatewayMessage msg)
        => JsonSerializer.Serialize(msg, GatewayJsonContext.Default.GatewayMessage);

    private static GatewayMessage? Deserialize(string json)
        => JsonSerializer.Deserialize(json, GatewayJsonContext.Default.GatewayMessage);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Serialize_ChatMessage_CamelCaseFields()
    {
        var msg  = new GatewayMessage { Type = MessageType.ChatMessage, Text = "hi" };
        var json = Serialize(msg);

        // Property names must be camelCase per GatewayJsonContext options
        Assert.Contains("\"type\"", json);
        Assert.Contains($"\"{MessageType.ChatMessage}\"", json);
        Assert.Contains("\"text\"", json);

        // PascalCase must NOT appear
        Assert.DoesNotContain("\"Type\"", json);
    }

    [Fact]
    public void Serialize_OmitsNullFields()
    {
        var msg  = new GatewayMessage { Type = MessageType.ResetSession };
        var json = Serialize(msg);

        // Null-valued optional properties must be absent from the JSON
        Assert.DoesNotContain("sessionId", json);
        Assert.DoesNotContain("text",      json);
        Assert.DoesNotContain("tool",      json);
        Assert.DoesNotContain("input",     json);
    }

    [Fact]
    public void Deserialize_AgentChunk_RoundTrip()
    {
        var original = new GatewayMessage
        {
            Type      = MessageType.AgentChunk,
            SessionId = "sess-abc",
            Text      = "chunk text",
            Tool      = null,
            Input     = null
        };

        var json         = Serialize(original);
        var deserialized = Deserialize(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Type,      deserialized!.Type);
        Assert.Equal(original.SessionId, deserialized.SessionId);
        Assert.Equal(original.Text,      deserialized.Text);
        Assert.Null(deserialized.Tool);
        Assert.Null(deserialized.Input);
    }

    [Fact]
    public void Deserialize_ExtraFields_AreIgnored()
    {
        const string json = """
            {
                "type": "agent_chunk",
                "sessionId": "s1",
                "unknownField": "should be ignored",
                "anotherExtra": 42
            }
            """;

        var exception = Record.Exception(() => Deserialize(json));
        Assert.Null(exception);

        var msg = Deserialize(json);
        Assert.NotNull(msg);
        Assert.Equal(MessageType.AgentChunk, msg!.Type);
        Assert.Equal("s1", msg.SessionId);
    }

    [Fact]
    public void MessageType_Constants_HaveExpectedValues()
    {
        // Server → Client
        Assert.Equal("agent_chunk",    MessageType.AgentChunk);
        Assert.Equal("agent_response", MessageType.AgentResponse);
        Assert.Equal("tool_call",      MessageType.ToolCall);
        Assert.Equal("tool_result",    MessageType.ToolResult);
        Assert.Equal("error",          MessageType.Error);
        Assert.Equal("reset_session",  MessageType.ResetSession);

        // Client → Server
        Assert.Equal("chat_message",   MessageType.ChatMessage);
    }

    [Fact]
    public void Serialize_AllNullableFields_RoundTrip()
    {
        var original = new GatewayMessage
        {
            Type      = MessageType.ToolCall,
            SessionId = "session-xyz",
            Text      = "run this",
            Tool      = "bash",
            Input     = "echo hello"
        };

        var json         = Serialize(original);
        var deserialized = Deserialize(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Type,      deserialized!.Type);
        Assert.Equal(original.SessionId, deserialized.SessionId);
        Assert.Equal(original.Text,      deserialized.Text);
        Assert.Equal(original.Tool,      deserialized.Tool);
        Assert.Equal(original.Input,     deserialized.Input);
    }
}
