using System.Reflection;
using DotnetClaw.Gateway;
using DotnetClaw.Web.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotnetClaw.Gateway.Tests;

public sealed class WebGatewayClientServiceTests
{
    // ── Factory helpers ───────────────────────────────────────────────────────

    private static WebGatewayClientService CreateService()
    {
        var options = Options.Create(new GatewayClientOptions());
        return new WebGatewayClientService(options, NullLogger<WebGatewayClientService>.Instance);
    }

    /// <summary>
    /// Invokes the private <c>DispatchAsync(GatewayMessage)</c> method via reflection.
    /// </summary>
    private static async Task DispatchAsync(WebGatewayClientService service, GatewayMessage message)
    {
        var method = typeof(WebGatewayClientService)
            .GetMethod("DispatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "DispatchAsync method not found on WebGatewayClientService. " +
                "Check that the method name has not changed.");

        await (Task)method.Invoke(service, new object[] { message })!;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Subscribe_RegistersHandler_CalledOnDispatch()
    {
        var service  = CreateService();
        var received = new List<GatewayMessage>();

        service.Subscribe("session-1", msg =>
        {
            received.Add(msg);
            return Task.CompletedTask;
        });

        var message = new GatewayMessage
        {
            Type      = MessageType.AgentChunk,
            SessionId = "session-1",
            Text      = "hello"
        };

        await DispatchAsync(service, message);

        Assert.Single(received);
        Assert.Equal(MessageType.AgentChunk, received[0].Type);
        Assert.Equal("hello",               received[0].Text);
    }

    [Fact]
    public async Task Subscribe_ReplacesHandler_LastWins()
    {
        var service   = CreateService();
        var firstCalled  = false;
        var secondCalled = false;

        service.Subscribe("session-2", _ =>
        {
            firstCalled = true;
            return Task.CompletedTask;
        });

        service.Subscribe("session-2", _ =>
        {
            secondCalled = true;
            return Task.CompletedTask;
        });

        await DispatchAsync(service, new GatewayMessage
        {
            Type      = MessageType.AgentResponse,
            SessionId = "session-2"
        });

        Assert.False(firstCalled,  "First handler should have been replaced");
        Assert.True(secondCalled,  "Last-registered handler must be called");
    }

    [Fact]
    public async Task Unsubscribe_RemovesHandler_NotCalledAfterRemoval()
    {
        var service = CreateService();
        var called  = false;

        service.Subscribe("session-3", _ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        service.Unsubscribe("session-3");

        await DispatchAsync(service, new GatewayMessage
        {
            Type      = MessageType.AgentChunk,
            SessionId = "session-3"
        });

        Assert.False(called, "Handler must not be called after Unsubscribe");
    }

    [Fact]
    public async Task Dispatch_NullSessionId_DoesNotThrow()
    {
        var service = CreateService();

        var message = new GatewayMessage
        {
            Type      = MessageType.Error,
            SessionId = null,
            Text      = "broadcast error"
        };

        var exception = await Record.ExceptionAsync(() => DispatchAsync(service, message));
        Assert.Null(exception);
    }

    [Fact]
    public async Task Dispatch_NoSubscriber_DoesNotThrow()
    {
        var service = CreateService();

        var message = new GatewayMessage
        {
            Type      = MessageType.AgentChunk,
            SessionId = "unknown-session-xyz"
        };

        var exception = await Record.ExceptionAsync(() => DispatchAsync(service, message));
        Assert.Null(exception);
    }

    [Fact]
    public async Task Dispatch_WrongSessionId_DoesNotCallHandler()
    {
        var service = CreateService();
        var called  = false;

        service.Subscribe("session-A", _ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await DispatchAsync(service, new GatewayMessage
        {
            Type      = MessageType.AgentChunk,
            SessionId = "session-B"
        });

        Assert.False(called, "Handler for session-A must not fire for session-B messages");
    }

    [Fact]
    public void IsConnected_NoSocket_ReturnsFalse()
    {
        var service = CreateService();
        // Service was never started — _socket is null, so IsConnected must be false
        Assert.False(service.IsConnected);
    }
}
