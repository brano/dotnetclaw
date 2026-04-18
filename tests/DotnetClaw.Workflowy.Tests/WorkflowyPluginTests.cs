using System.Text.Json;
using DotnetClaw.Workflowy.Engine;
using DotnetClaw.Workflowy.Plugin;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetClaw.Workflowy.Tests;

/// <summary>
/// Unit tests for WorkflowyPlugin. Uses hand-written fakes for IWorkflowEngine and
/// IApprovalNotifier — Moq is not available in this test project.
/// </summary>
public sealed class WorkflowyPluginTests
{
    private readonly FakeWorkflowEngine _engine = new();
    private readonly FakeApprovalNotifier _notifier = new();
    private readonly WorkflowyPlugin _sut;

    public WorkflowyPluginTests()
    {
        _sut = new WorkflowyPlugin(_engine, _notifier, NullLogger<WorkflowyPlugin>.Instance);
    }

    // ── RunWorkflowAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task RunWorkflowAsync_InvalidJson_ReturnsErrorEnvelope()
    {
        var result = await _sut.RunWorkflowAsync("not-valid-json{{{");

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.True(json.TryGetProperty("error", out var errProp));
        Assert.StartsWith("Invalid request JSON:", errProp.GetString());
    }

    [Fact]
    public async Task RunWorkflowAsync_WrongAction_ReturnsError()
    {
        var requestJson = """{"action":"resume","pipeline":"/some/file.yaml","timeoutMs":5000}""";

        var result = await _sut.RunWorkflowAsync(requestJson);

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Equal("action must be 'run'", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RunWorkflowAsync_EmptyPipeline_ReturnsError()
    {
        var requestJson = """{"action":"run","pipeline":"","timeoutMs":5000}""";

        var result = await _sut.RunWorkflowAsync(requestJson);

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Equal("pipeline is required", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RunWorkflowAsync_EngineReturnsOk_ReturnsOkJson()
    {
        _engine.RunResult = new WorkflowyResponse(true, "ok");
        var requestJson = """{"action":"run","pipeline":"/some/workflow.yaml","timeoutMs":5000}""";

        var result = await _sut.RunWorkflowAsync(requestJson);

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("ok", json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task RunWorkflowAsync_EngineThrows_ReturnsErrorEnvelope()
    {
        _engine.RunException = new InvalidOperationException("database unavailable");
        var requestJson = """{"action":"run","pipeline":"/some/workflow.yaml","timeoutMs":5000}""";

        var result = await _sut.RunWorkflowAsync(requestJson);

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Contains("database unavailable", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RunWorkflowAsync_NeedsApproval_NotifiesApprovalNotifier()
    {
        _engine.RunResult = new WorkflowyResponse(true, "needs_approval")
        {
            RequiresApproval = new ApprovalRequest(
                "approval_request",
                "Deploy to prod?",
                ["service-a", "service-b"],
                "tok_abc123"),
        };
        var requestJson = """{"action":"run","pipeline":"/deploy/workflow.yaml","timeoutMs":10000}""";

        await _sut.RunWorkflowAsync(requestJson);

        Assert.Single(_notifier.Pending);
        var pending = _notifier.Pending[0];
        Assert.Equal("tok_abc123", pending.Token);
        Assert.Equal("Deploy to prod?", pending.Prompt);
    }

    [Fact]
    public async Task RunWorkflowAsync_ParsesPipelineArgs()
    {
        _engine.RunResult = new WorkflowyResponse(true, "ok");
        var requestJson = """{"action":"run","pipeline":"/some/file.yaml --env prod --region us-east","timeoutMs":5000}""";

        await _sut.RunWorkflowAsync(requestJson);

        Assert.Equal("/some/file.yaml", _engine.LastRunPath);
        Assert.NotNull(_engine.LastRunArgs);
        Assert.Equal("prod", _engine.LastRunArgs["env"]);
        Assert.Equal("us-east", _engine.LastRunArgs["region"]);
    }

    // ── ResumeWorkflowAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task ResumeWorkflowAsync_InvalidJson_ReturnsError()
    {
        var result = await _sut.ResumeWorkflowAsync("{{broken");

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.StartsWith("Invalid request JSON:", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ResumeWorkflowAsync_WrongAction_ReturnsError()
    {
        var requestJson = """{"action":"run","token":"tok_xyz","approve":true}""";

        var result = await _sut.ResumeWorkflowAsync(requestJson);

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("action must be 'resume'", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ResumeWorkflowAsync_MissingToken_ReturnsError()
    {
        var requestJson = """{"action":"resume","approve":true}""";

        var result = await _sut.ResumeWorkflowAsync(requestJson);

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("token is required", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ResumeWorkflowAsync_ApproveTrue_CallsResumeWithApprovedTrue()
    {
        _engine.ResumeResult = new WorkflowyResponse(true, "ok");
        var requestJson = """{"action":"resume","token":"tok_approve_me","approve":true}""";

        await _sut.ResumeWorkflowAsync(requestJson);

        Assert.Equal("tok_approve_me", _engine.LastResumeToken);
        Assert.True(_engine.LastApproved);
    }

    [Fact]
    public async Task ResumeWorkflowAsync_ApproveFalse_CallsResumeWithApprovedFalse()
    {
        _engine.ResumeResult = new WorkflowyResponse(false, "cancelled");
        var requestJson = """{"action":"resume","token":"tok_reject_me","approve":false}""";

        await _sut.ResumeWorkflowAsync(requestJson);

        Assert.Equal("tok_reject_me", _engine.LastResumeToken);
        Assert.False(_engine.LastApproved);
    }

    [Fact]
    public async Task ResumeWorkflowAsync_Success_NotifiesResumed()
    {
        _engine.ResumeResult = new WorkflowyResponse(true, "ok");
        var requestJson = """{"action":"resume","token":"tok_notify_me","approve":true}""";

        await _sut.ResumeWorkflowAsync(requestJson);

        Assert.Single(_notifier.Resumed);
        var (token, status) = _notifier.Resumed[0];
        Assert.Equal("tok_notify_me", token);
        Assert.Equal("ok", status);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Hand-written fakes (Moq is not available in this test project)
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class FakeWorkflowEngine : IWorkflowEngine
{
    public WorkflowyResponse? RunResult { get; set; }
    public Exception? RunException { get; set; }
    public WorkflowyResponse? ResumeResult { get; set; }

    public string? LastRunPath { get; private set; }
    public Dictionary<string, string>? LastRunArgs { get; private set; }
    public string? LastResumeToken { get; private set; }
    public bool LastApproved { get; private set; }

    public Task<WorkflowyResponse> RunAsync(
        string workflowPath,
        Dictionary<string, string> args,
        int timeoutMs,
        CancellationToken ct)
    {
        LastRunPath = workflowPath;
        LastRunArgs = args;
        if (RunException is not null) throw RunException;
        return Task.FromResult(RunResult ?? WorkflowyResponse.Failure("not configured"));
    }

    public Task<WorkflowyResponse> ResumeAsync(string resumeToken, bool approved, CancellationToken ct)
    {
        LastResumeToken = resumeToken;
        LastApproved = approved;
        return Task.FromResult(ResumeResult ?? WorkflowyResponse.Failure("not configured"));
    }
}

internal sealed class FakeApprovalNotifier : IApprovalNotifier
{
    public List<PendingApprovalDto> Pending { get; } = [];
    public List<(string token, string status)> Resumed { get; } = [];

    public Task NotifyPendingAsync(PendingApprovalDto approval, CancellationToken ct = default)
    {
        Pending.Add(approval);
        return Task.CompletedTask;
    }

    public Task NotifyResumedAsync(string token, string status, CancellationToken ct = default)
    {
        Resumed.Add((token, status));
        return Task.CompletedTask;
    }
}
