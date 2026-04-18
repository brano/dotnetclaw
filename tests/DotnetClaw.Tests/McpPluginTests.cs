using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using DotnetClaw.Config;
using DotnetClaw.Mcp;
using DotnetClaw.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Moq;
using Xunit;

namespace DotnetClaw.Tests;

// ============================================================================
//  McpPlugin Tests
//  Uses Moq to mock McpConnectionManager and IMcpClient — no real MCP servers.
// ============================================================================

public class McpPluginTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static McpPlugin CreatePlugin(
        Mock<IMcpConnectionManager>? managerMock = null,
        McpOptions? opts = null)
    {
        var manager = managerMock ?? new Mock<IMcpConnectionManager>();

        var kernelLoader = new Mock<IMcpKernelLoader>();

        return new McpPlugin(
            manager.Object,
            kernelLoader.Object,
            Options.Create(opts ?? new McpOptions()),
            NullLogger<McpPlugin>.Instance);
    }

    private static McpServerStatus ConnectedStatus(
        string name, int toolCount = 3) => new()
    {
        Name = name,
        Description = $"{name} server",
        Transport = McpTransport.Stdio,
        Connected = true,
        ToolCount = toolCount,
        ConnectedAt = DateTimeOffset.UtcNow,
    };

    private static McpServerStatus FailedStatus(string name) => new()
    {
        Name = name,
        Description = $"{name} server",
        Transport = McpTransport.Stdio,
        Connected = false,
        Error = "Connection refused",
    };

    // ── mcp_list_servers ──────────────────────────────────────────────────────

    [Fact]
    public void ListServers_NoServers_ReturnsNotConfiguredMessage()
    {
        var mgr = new Mock<IMcpConnectionManager>();
        mgr.Setup(m => m.GetStatuses()).Returns([]);

        var plugin = CreatePlugin(mgr);
        var result = plugin.ListServers();

        Assert.Contains("No MCP servers", result);
    }

    [Fact]
    public void ListServers_MixedStatus_ShowsBothConnectedAndFailed()
    {
        var mgr = new Mock<IMcpConnectionManager>();
        mgr.Setup(m => m.GetStatuses()).Returns([
            ConnectedStatus("filesystem", 5),
            FailedStatus("github"),
        ]);

        var plugin = CreatePlugin(mgr);
        var result = plugin.ListServers();

        Assert.Contains("filesystem", result);
        Assert.Contains("✅", result);
        Assert.Contains("5 tool(s)", result);
        Assert.Contains("github", result);
        Assert.Contains("❌", result);
        Assert.Contains("Connection refused", result);
    }

    // ── mcp_list_tools ────────────────────────────────────────────────────────

    [Fact]
    public async Task ListTools_ServerNotConnected_ReturnsError()
    {
        var mgr = new Mock<IMcpConnectionManager>();
        mgr.Setup(m => m.GetClient("missing")).Returns((IMcpClient?)null);

        var plugin = CreatePlugin(mgr);
        var result = await plugin.ListToolsAsync("missing");

        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("missing", result);
    }

    [Fact]
    public async Task ListTools_ConnectedServer_ReturnsToolList()
    {
        var tools = new List<McpClientTool>
        {
            CreateTool("read_file",  "Read a file from the filesystem",  [("path", "string", true)]),
            CreateTool("write_file", "Write content to a file",          [("path", "string", true), ("content", "string", true)]),
            CreateTool("list_dir",   "List directory contents",          [("path", "string", false)]),
        };

        var clientMock = new Mock<IMcpClient>();
        clientMock.Setup(c => c.ListToolsAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(tools);

        var mgr = new Mock<IMcpConnectionManager>();
        mgr.Setup(m => m.GetClient("filesystem")).Returns(clientMock.Object);

        var plugin = CreatePlugin(mgr);
        var result = await plugin.ListToolsAsync("filesystem");

        Assert.Contains("read_file", result);
        Assert.Contains("write_file", result);
        Assert.Contains("list_dir", result);
        Assert.Contains("Read a file", result);
        Assert.Contains("path", result);
        Assert.Contains("(required)", result);
    }

    [Fact]
    public async Task ListTools_EmptyToolList_ReturnsNoToolsMessage()
    {
        var clientMock = new Mock<IMcpClient>();
        clientMock.Setup(c => c.ListToolsAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync([]);

        var mgr = new Mock<IMcpConnectionManager>();
        mgr.Setup(m => m.GetClient("empty")).Returns(clientMock.Object);

        var plugin = CreatePlugin(mgr);
        var result = await plugin.ListToolsAsync("empty");

        Assert.Contains("no tools", result.ToLower());
    }

    // ── mcp_call_tool ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CallTool_ServerNotConnected_ReturnsError()
    {
        var mgr = new Mock<IMcpConnectionManager>();
        mgr.Setup(m => m.GetClient("gone")).Returns((IMcpClient?)null);

        var plugin = CreatePlugin(mgr);
        var result = await plugin.CallToolAsync("gone", "some_tool");

        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task CallTool_InvalidJson_ReturnsJsonError()
    {
        var clientMock = new Mock<IMcpClient>();
        var mgr = new Mock<IMcpConnectionManager>();
        mgr.Setup(m => m.GetClient("fs")).Returns(clientMock.Object);

        var plugin = CreatePlugin(mgr);
        var result = await plugin.CallToolAsync("fs", "read_file", "{not valid json");

        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("JSON", result);
    }

    [Fact]
    public async Task CallTool_Success_ReturnsTextContent()
    {
        var callResult = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "Hello from MCP tool!" }],
        };

        var clientMock = new Mock<IMcpClient>();
        clientMock
            .Setup(c => c.CallToolAsync(
                "echo",
                It.IsAny<IReadOnlyDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(callResult);

        var mgr = new Mock<IMcpConnectionManager>();
        mgr.Setup(m => m.GetClient("test")).Returns(clientMock.Object);

        var plugin = CreatePlugin(mgr);
        var result = await plugin.CallToolAsync("test", "echo", "{}");

        Assert.Contains("Hello from MCP tool!", result);
    }

    [Fact]
    public async Task CallTool_EmptyResult_ReturnsEmptyResultString()
    {
        var callResult = new CallToolResult { Content = [] };

        var clientMock = new Mock<IMcpClient>();
        clientMock
            .Setup(c => c.CallToolAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(callResult);

        var mgr = new Mock<IMcpConnectionManager>();
        mgr.Setup(m => m.GetClient("test")).Returns(clientMock.Object);

        var plugin = CreatePlugin(mgr);
        var result = await plugin.CallToolAsync("test", "noop", "{}");

        Assert.Contains("empty result", result.ToLower());
    }

    // ── mcp_reconnect ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Reconnect_SuccessfulReconnect_ReturnsOk()
    {
        var mgr = new Mock<IMcpConnectionManager>();
        mgr.Setup(m => m.ReconnectAsync("filesystem", It.IsAny<CancellationToken>()))
           .ReturnsAsync(ConnectedStatus("filesystem", 5));

        var kernelLoader = new Mock<IMcpKernelLoader>();

        var plugin = new McpPlugin(
            mgr.Object, kernelLoader.Object,
            Options.Create(new McpOptions()),
            NullLogger<McpPlugin>.Instance);

        var kernel = Microsoft.SemanticKernel.Kernel.CreateBuilder().Build();
        var result = await plugin.ReconnectAsync("filesystem", kernel);

        Assert.StartsWith("[OK]", result);
        Assert.Contains("5 tool(s)", result);
    }

    [Fact]
    public async Task Reconnect_FailedReconnect_ReturnsError()
    {
        var mgr = new Mock<IMcpConnectionManager>();
        mgr.Setup(m => m.ReconnectAsync("flaky", It.IsAny<CancellationToken>()))
           .ReturnsAsync(FailedStatus("flaky"));

        var kernelLoader = new Mock<IMcpKernelLoader>();

        var plugin = new McpPlugin(
            mgr.Object, kernelLoader.Object,
            Options.Create(new McpOptions()),
            NullLogger<McpPlugin>.Instance);

        var kernel = Microsoft.SemanticKernel.Kernel.CreateBuilder().Build();
        var result = await plugin.ReconnectAsync("flaky", kernel);

        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("Connection refused", result);
    }

    // ── mcp_list_resources ────────────────────────────────────────────────────

    [Fact]
    public async Task ListResources_ServerNotConnected_ReturnsError()
    {
        var mgr = new Mock<IMcpConnectionManager>();
        mgr.Setup(m => m.GetClient("gone")).Returns((IMcpClient?)null);

        var plugin = CreatePlugin(mgr);
        var result = await plugin.ListResourcesAsync("gone");

        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task ListResources_ReturnsFormattedList()
    {
        // McpClientResource requires an McpClient (SDK type) for construction.
        // We use a minimal stub purely to satisfy the constructor — it is never called.
        var stub = new McpClientStub();

        IList<McpClientResource> resources =
        [
            new McpClientResource(stub, new Resource { Name = "README.md",   Uri = "file:///project/README.md",   MimeType = "text/markdown",   Description = "Project readme" }),
            new McpClientResource(stub, new Resource { Name = "config.json", Uri = "file:///project/config.json", MimeType = "application/json" }),
        ];

        var clientMock = new Mock<IMcpClient>();
        clientMock.Setup(c => c.ListResourcesAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(resources);

        var mgr = new Mock<IMcpConnectionManager>();
        mgr.Setup(m => m.GetClient("fs")).Returns(clientMock.Object);

        var plugin = CreatePlugin(mgr);
        var result = await plugin.ListResourcesAsync("fs");

        Assert.Contains("README.md", result);
        Assert.Contains("file:///project/README.md", result);
        Assert.Contains("config.json", result);
        Assert.Contains("Project readme", result);
    }

    // ── mcp_read_resource ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReadResource_ReturnsTextContent()
    {
        var readResult = new ReadResourceResult
        {
            Contents = [new TextResourceContents { Uri = "file:///test.md", Text = "# Hello World" }]
        };

        var clientMock = new Mock<IMcpClient>();
        clientMock.Setup(c => c.ReadResourceAsync("file:///test.md", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(readResult);

        var mgr = new Mock<IMcpConnectionManager>();
        mgr.Setup(m => m.GetClient("fs")).Returns(clientMock.Object);

        var plugin = CreatePlugin(mgr);
        var result = await plugin.ReadResourceAsync("fs", "file:///test.md");

        Assert.Contains("# Hello World", result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="McpClientTool"/> wrapping a <see cref="Tool"/> with
    /// the given name, description and parameter schema.
    /// </summary>
    private static McpClientTool CreateTool(
        string name,
        string description,
        (string paramName, string type, bool required)[] parameters)
    {
        var props = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var (pName, pType, pRequired) in parameters)
        {
            props[pName] = new { type = pType, description = pType };
            if (pRequired) required.Add(pName);
        }

        var schemaJson = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = props,
            required,
        });

        var protocolTool = new Tool
        {
            Name = name,
            Description = description,
            InputSchema = schemaJson,
        };

        // McpClientStub exists solely to satisfy the McpClientTool constructor.
        // Its methods are never invoked in list-tools tests.
        return new McpClientTool(new McpClientStub(), protocolTool);
    }
}

// ============================================================================
//  McpClientStub — minimal McpClient subclass for McpClientTool construction.
//  All abstract members throw NotImplementedException; the stub is used only
//  as a constructor argument and is never invoked during tests.
// ============================================================================

#pragma warning disable MCPEXP002
[SuppressMessage("Usage", "MCPEXP002", Justification = "Test double")]
internal sealed class McpClientStub : ModelContextProtocol.Client.McpClient
{
    public override string? SessionId => null;
    public override string? NegotiatedProtocolVersion => null;
    public override Task<ClientCompletionDetails> Completion =>
        Task.FromResult<ClientCompletionDetails>(new StdioClientCompletionDetails());
    public override ServerCapabilities ServerCapabilities => new() { Tools = new ToolsCapability() };
    public override Implementation ServerInfo => new() { Name = "stub", Version = "0" };
    public override string? ServerInstructions => null;

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public override Task SendMessageAsync(
        ModelContextProtocol.Protocol.JsonRpcMessage message,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    public override IAsyncDisposable RegisterNotificationHandler(
        string method,
        Func<ModelContextProtocol.Protocol.JsonRpcNotification, CancellationToken, ValueTask> handler)
        => new NullAsyncDisposable();
    public override Task<ModelContextProtocol.Protocol.JsonRpcResponse> SendRequestAsync(
        ModelContextProtocol.Protocol.JsonRpcRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Stub — not expected to be called in unit tests.");
}
#pragma warning restore MCPEXP002

internal sealed class NullAsyncDisposable : IAsyncDisposable
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
