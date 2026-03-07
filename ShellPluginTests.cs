using DotnetClaw.Config;
using DotnetClaw.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotnetClaw.Tests;

public class ShellPluginTests
{
    private ShellPlugin CreatePlugin(
        List<string>? allowed = null,
        List<string>? blocked = null)
    {
        var opts = Options.Create(new DotnetClawOptions
        {
            WorkingDirectory = Directory.GetCurrentDirectory(),
            AllowedShellCommands = allowed ?? [],
            BlockedShellCommands = blocked ?? ["rm -rf /"],
        });
        return new ShellPlugin(opts, NullLogger<ShellPlugin>.Instance);
    }

    [Fact]
    public async Task RunCommandAsync_EchoCommand_ReturnsOutput()
    {
        var plugin = CreatePlugin();
        var result = await plugin.RunCommandAsync("echo Hello DotnetClaw");

        Assert.True(result.Success);
        Assert.Contains("Hello DotnetClaw", result.Stdout);
    }

    [Fact]
    public async Task RunCommandAsync_BlockedCommand_ReturnsBlockedResult()
    {
        var plugin = CreatePlugin(blocked: ["rm -rf /"]);
        var result = await plugin.RunCommandAsync("rm -rf /");

        Assert.False(result.Success);
        Assert.Contains("BLOCKED", result.ErrorMessage);
    }

    [Fact]
    public async Task RunCommandAsync_NotInAllowList_ReturnsBlockedResult()
    {
        var plugin = CreatePlugin(allowed: ["echo", "ls"]);
        var result = await plugin.RunCommandAsync("curl https://example.com");

        Assert.False(result.Success);
        Assert.Contains("BLOCKED", result.ErrorMessage);
    }

    [Fact]
    public async Task RunCommandAsync_AllowListEmpty_AllowsAll()
    {
        // Empty allow-list = dev mode, allow everything
        var plugin = CreatePlugin(allowed: []);
        var result = await plugin.RunCommandAsync("echo test");

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ListDirectoryAsync_CurrentDirectory_ReturnsListing()
    {
        var plugin = CreatePlugin();
        var listing = await plugin.ListDirectoryAsync(Directory.GetCurrentDirectory());

        Assert.NotEmpty(listing);
        Assert.Contains("📁", listing);
    }

    [Fact]
    public async Task ListDirectoryAsync_NonExistentPath_ReturnsError()
    {
        var plugin = CreatePlugin();
        var listing = await plugin.ListDirectoryAsync("/this/path/does/not/exist");

        Assert.Contains("not found", listing, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunCommandAsync_Timeout_ReturnsTimeoutResult()
    {
        var plugin = CreatePlugin();
        // 1 second timeout on a sleep command
        var result = await plugin.RunCommandAsync("sleep 10", timeoutSeconds: 1);

        Assert.False(result.Success);
        Assert.Contains("TIMEOUT", result.ErrorMessage);
    }
}
