using DotnetClaw.Config;
using DotnetClaw.Plugins;
using DotnetClaw.UI;
using DotnetClaw.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace DotnetClaw.Tests;

// ============================================================================
//  Helpers
// ============================================================================

/// <summary>
/// A configurable fake for <see cref="ICursorProcessRunner"/> that doesn't
/// spawn real processes — perfect for unit tests.
/// </summary>
internal sealed class FakeCursorProcessRunner : ICursorProcessRunner
{
    public List<CursorInvocation> ReceivedInvocations { get; } = [];

    /// <summary>Configure the output the fake should return.</summary>
    public CursorProcessOutput NextOutput { get; set; } = new()
    {
        ExitCode = 0,
        Stdout = "Agent completed successfully.",
        Stderr = string.Empty,
        TimedOut = false,
        ProcessError = false,
        StartedAt = DateTimeOffset.UtcNow,
        FinishedAt = DateTimeOffset.UtcNow.AddSeconds(2),
    };

    public Task<CursorProcessOutput> RunAsync(
        CursorInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ReceivedInvocations.Add(invocation);
        return Task.FromResult(NextOutput);
    }
}

/// <summary>Null renderer — swallows all console output during tests.</summary>
internal sealed class NullRenderer : IConsoleRenderer
{
    public void BeginAssistantTurn() { }
    public void WriteChunk(string text) { }
    public void EndAssistantTurn() { }
    public void WriteWarning(string message) { }
    public void WriteToolCall(string toolName, string input) { }
    public void WriteToolResult(string toolName, bool success, string preview) { }
    public void WriteError(string message) { }
    public void WriteBanner() { }
    public void WriteWorkspaceStatus(WorkspaceLoadResult result) { }
    public string PromptUser(string prompt = "> ") => "yes"; // auto-confirm
}

// ============================================================================
//  Tests
// ============================================================================

public class CursorPluginTests
{
    private readonly FakeCursorProcessRunner _runner = new();
    private readonly NullRenderer _renderer = new();

    private CursorPlugin CreatePlugin(
        string executablePath = "agent",
        bool requireConfirmation = false,
        bool autoApprove = false,
        string? model = null,
        string extraFlags = "")
    {
        var opts = Options.Create(new CursorOptions
        {
            ExecutablePath = executablePath,
            DefaultTimeoutSeconds = 60,
            RequireConfirmationForAgentMode = requireConfirmation,
            AutoApproveInAgentMode = autoApprove,
            Model = model,
            ExtraFlags = extraFlags,
        });
        return new CursorPlugin(_runner, opts, _renderer, NullLogger<CursorPlugin>.Instance);
    }

    // ── cursor_ask ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CursorAsk_CorrectModeFlag_InArguments()
    {
        var plugin = CreatePlugin();
        await plugin.CursorAskAsync("What does AuthService do?", workspacePath: "/tmp/repo");

        Assert.Single(_runner.ReceivedInvocations);
        var inv = _runner.ReceivedInvocations[0];
        Assert.Contains("--mode=ask", inv.Arguments);
    }

    [Fact]
    public async Task CursorAsk_PromptIncludedInArguments()
    {
        var plugin = CreatePlugin();
        await plugin.CursorAskAsync("How does caching work?", workspacePath: "/tmp/repo");

        var inv = _runner.ReceivedInvocations[0];
        Assert.Contains("How does caching work?", inv.Arguments);
    }

    [Fact]
    public async Task CursorAsk_WorkspaceIncludedInArguments()
    {
        var plugin = CreatePlugin();
        await plugin.CursorAskAsync("Explain login flow", workspacePath: "/my/codebase");

        var inv = _runner.ReceivedInvocations[0];
        Assert.Contains("/my/codebase", inv.Arguments);
        Assert.Equal("/my/codebase", inv.WorkingDirectory);
    }

    [Fact]
    public async Task CursorAsk_DoesNotPassYesFlag()
    {
        var plugin = CreatePlugin(autoApprove: true); // --yes should never appear in ask
        await plugin.CursorAskAsync("Explain index", workspacePath: "/tmp/repo");

        var inv = _runner.ReceivedInvocations[0];
        Assert.DoesNotContain("--yes", inv.Arguments);
    }

    // ── cursor_plan ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CursorPlan_CorrectModeFlag()
    {
        var plugin = CreatePlugin();
        await plugin.CursorPlanAsync("Plan JWT auth feature", workspacePath: "/tmp/repo");

        Assert.Contains("--mode=plan", _runner.ReceivedInvocations[0].Arguments);
    }

    [Fact]
    public async Task CursorPlan_DoesNotPassYesFlag()
    {
        var plugin = CreatePlugin(autoApprove: true);
        await plugin.CursorPlanAsync("Plan refactor", workspacePath: "/tmp/repo");

        Assert.DoesNotContain("--yes", _runner.ReceivedInvocations[0].Arguments);
    }

    // ── cursor_agent ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CursorAgent_CorrectModeFlag()
    {
        var plugin = CreatePlugin(requireConfirmation: false);
        await plugin.CursorAgentAsync("Add unit tests for AuthService", workspacePath: "/tmp/repo");

        Assert.Contains("--mode=agent", _runner.ReceivedInvocations[0].Arguments);
    }

    [Fact]
    public async Task CursorAgent_AutoApprove_PassesYesFlag()
    {
        var plugin = CreatePlugin(requireConfirmation: false, autoApprove: true);
        await plugin.CursorAgentAsync("Refactor", workspacePath: "/tmp/repo");

        Assert.Contains("--yes", _runner.ReceivedInvocations[0].Arguments);
    }

    [Fact]
    public async Task CursorAgent_NoAutoApprove_DoesNotPassYesFlag()
    {
        var plugin = CreatePlugin(requireConfirmation: false, autoApprove: false);
        await plugin.CursorAgentAsync("Refactor", workspacePath: "/tmp/repo");

        Assert.DoesNotContain("--yes", _runner.ReceivedInvocations[0].Arguments);
    }

    // ── cursor_run ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CursorRun_AgentMode_CorrectFlag()
    {
        var plugin = CreatePlugin();
        await plugin.CursorRunAsync("Do something", mode: "agent", workspacePath: "/tmp/repo");

        Assert.Contains("--mode=agent", _runner.ReceivedInvocations[0].Arguments);
    }

    [Fact]
    public async Task CursorRun_PlanMode_CorrectFlag()
    {
        var plugin = CreatePlugin();
        await plugin.CursorRunAsync("Plan something", mode: "plan", workspacePath: "/tmp/repo");

        Assert.Contains("--mode=plan", _runner.ReceivedInvocations[0].Arguments);
    }

    [Fact]
    public async Task CursorRun_AskMode_CorrectFlag()
    {
        var plugin = CreatePlugin();
        await plugin.CursorRunAsync("Ask something", mode: "ask", workspacePath: "/tmp/repo");

        Assert.Contains("--mode=ask", _runner.ReceivedInvocations[0].Arguments);
    }

    [Fact]
    public async Task CursorRun_InvalidMode_ThrowsArgumentException()
    {
        var plugin = CreatePlugin();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            plugin.CursorRunAsync("prompt", mode: "INVALID", workspacePath: "/tmp"));
    }

    [Fact]
    public async Task CursorRun_ModelOverride_IncludedInArgs()
    {
        var plugin = CreatePlugin();
        await plugin.CursorRunAsync("Q", mode: "ask", workspacePath: "/tmp", model: "claude-3-5-sonnet");

        Assert.Contains("--model", _runner.ReceivedInvocations[0].Arguments);
        Assert.Contains("claude-3-5-sonnet", _runner.ReceivedInvocations[0].Arguments);
    }

    [Fact]
    public async Task CursorRun_ExtraFlagsIncludedInArgs()
    {
        var plugin = CreatePlugin(extraFlags: "--quiet");
        await plugin.CursorRunAsync("Q", mode: "ask", workspacePath: "/tmp");

        Assert.Contains("--quiet", _runner.ReceivedInvocations[0].Arguments);
    }

    [Fact]
    public async Task CursorRun_PerCallExtraFlagsOverride_IncludedInArgs()
    {
        var plugin = CreatePlugin();
        await plugin.CursorRunAsync("Q", mode: "ask", workspacePath: "/tmp", extraFlags: "--verbose");

        Assert.Contains("--verbose", _runner.ReceivedInvocations[0].Arguments);
    }

    // ── Model from config ──────────────────────────────────────────────────────

    [Fact]
    public async Task CursorAsk_ModelFromConfig_IncludedInArgs()
    {
        var plugin = CreatePlugin(model: "gpt-4o");
        await plugin.CursorAskAsync("Q?", workspacePath: "/tmp");

        Assert.Contains("--model", _runner.ReceivedInvocations[0].Arguments);
        Assert.Contains("gpt-4o", _runner.ReceivedInvocations[0].Arguments);
    }

    // ── Result mapping ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CursorAsk_SuccessfulRun_ResultContainsOutput()
    {
        _runner.NextOutput = _runner.NextOutput with
        {
            ExitCode = 0,
            Stdout = "The AuthService handles authentication via JWT tokens.",
        };
        var plugin = CreatePlugin();
        var result = await plugin.CursorAskAsync("What is AuthService?", workspacePath: "/tmp");

        Assert.Contains("AuthService", result);
        Assert.Contains("Success   : True", result);
    }

    [Fact]
    public async Task CursorAsk_FailedRun_ResultShowsFailure()
    {
        _runner.NextOutput = _runner.NextOutput with { ExitCode = 1 };
        var plugin = CreatePlugin();
        var result = await plugin.CursorAskAsync("Q?", workspacePath: "/tmp");

        Assert.Contains("Success   : False", result);
    }

    [Fact]
    public async Task CursorAsk_TimedOut_ReturnsCancelledResult()
    {
        _runner.NextOutput = _runner.NextOutput with
        {
            TimedOut = true, ExitCode = -2
        };
        var plugin = CreatePlugin();
        var result = await plugin.CursorAskAsync("Q?", workspacePath: "/tmp");

        Assert.Contains("CANCELLED", result);
    }

    [Fact]
    public async Task CursorAsk_ProcessError_ReturnsErrorResult()
    {
        _runner.NextOutput = _runner.NextOutput with
        {
            ProcessError = true, ProcessErrorMessage = "File not found"
        };
        var plugin = CreatePlugin();
        var result = await plugin.CursorAskAsync("Q?", workspacePath: "/tmp");

        Assert.Contains("PROCESS ERROR", result);
        Assert.Contains("File not found", result);
    }

    // ── Timeout passthrough ────────────────────────────────────────────────────

    [Fact]
    public async Task CursorAsk_CustomTimeout_PassedToRunner()
    {
        var plugin = CreatePlugin();
        await plugin.CursorAskAsync("Q?", workspacePath: "/tmp", timeoutSeconds: 120);

        Assert.Equal(120, _runner.ReceivedInvocations[0].TimeoutSeconds);
    }

    [Fact]
    public async Task CursorAsk_NullTimeout_UsesDefault()
    {
        var plugin = CreatePlugin(); // default is 60 in test helper
        await plugin.CursorAskAsync("Q?", workspacePath: "/tmp", timeoutSeconds: null);

        Assert.Equal(60, _runner.ReceivedInvocations[0].TimeoutSeconds);
    }

    // ── Prompt quoting ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CursorAsk_PromptWithSpaces_IsQuoted()
    {
        var plugin = CreatePlugin();
        await plugin.CursorAskAsync("What does method DoStuff() return?", workspacePath: "/tmp");

        var args = _runner.ReceivedInvocations[0].Arguments;
        // The prompt should be wrapped in quotes
        Assert.Contains("\"What does method DoStuff() return?\"", args);
    }

    [Fact]
    public async Task CursorAsk_PromptWithDoubleQuotes_IsEscaped()
    {
        var plugin = CreatePlugin();
        await plugin.CursorAskAsync("What is \"HttpClient\" used for?", workspacePath: "/tmp");

        var args = _runner.ReceivedInvocations[0].Arguments;
        Assert.Contains("\\\"HttpClient\\\"", args);
    }
}
