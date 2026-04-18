using DotnetClaw.Workflowy.Config;
using DotnetClaw.Workflowy.Data;
using DotnetClaw.Workflowy.Engine;
using DotnetClaw.Workflowy.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotnetClaw.Workflowy.Tests;

/// <summary>
/// Additional integration tests for WorkflowEngine using a real SQLite DB in a temp file.
/// Covers multi-step runs, conditions, approval gates, resume paths, failure modes, and arg
/// variable resolution.
/// </summary>
public sealed class WorkflowEngineAdditionalTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IDbContextFactory<WorkflowyDbContext> _dbFactory;
    private readonly WorkflowEngine _engine;
    private readonly WorkflowLoader _loader = new();
    private readonly VariableResolver _resolver = new();
    private readonly string _workflowDir;

    public WorkflowEngineAdditionalTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"workflowy_addl_{Guid.NewGuid():N}.db");
        _workflowDir = Path.Combine(Path.GetTempPath(), $"wf_addl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workflowDir);

        var opts = new DbContextOptionsBuilder<WorkflowyDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        _dbFactory = new AdditionalTestDbContextFactory(opts);

        using var db = _dbFactory.CreateDbContext();
        db.Database.EnsureCreated();

        var wOpts = Options.Create(new WorkflowyOptions
        {
            DatabasePath = _dbPath,
            DefaultStepTimeoutSeconds = 10,
            MaxStepTimeoutSeconds = 30,
        });

        var executor = new StepExecutor(wOpts, _resolver, NullLogger<StepExecutor>.Instance);
        var dispatcher = new PipelineDispatcher(NullLogger<PipelineDispatcher>.Instance);

        _engine = new WorkflowEngine(
            _dbFactory, _loader, executor, dispatcher, _resolver,
            NullLogger<WorkflowEngine>.Instance);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_MultiStepWorkflow_AllStepsRun()
    {
        var yaml = """
            name: multi_step
            steps:
              - name: step_a
                run: "echo a"
              - name: step_b
                run: "echo b"
              - name: step_c
                run: "echo c"
            """;
        var path = WriteWorkflow("multi.yaml", yaml);

        var response = await _engine.RunAsync(path, [], 10000, CancellationToken.None);

        Assert.True(response.Ok);
        Assert.Equal("ok", response.Status);

        using var db = _dbFactory.CreateDbContext();
        var run = await db.WorkflowRuns.Include(r => r.StepResults).FirstAsync();
        Assert.Equal(WorkflowRunStatus.Completed, run.Status);
        Assert.Equal(3, run.StepResults.Count);
        Assert.All(run.StepResults, sr => Assert.Equal(StepResultStatus.Success, sr.Status));
    }

    [Fact]
    public async Task RunAsync_InvalidWorkflowPath_ReturnsFailure()
    {
        var nonExistentPath = Path.Combine(_workflowDir, "does_not_exist.yaml");

        var response = await _engine.RunAsync(nonExistentPath, [], 5000, CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal("error", response.Status);
        Assert.False(string.IsNullOrWhiteSpace(response.Error));
    }

    [Fact]
    public async Task RunAsync_WorkflowWithCondition_SkipsStepWhenConditionFalse()
    {
        var yaml = """
            name: cond_skip
            steps:
              - name: first
                run: "echo first"
              - name: conditional
                run: "echo should_be_skipped"
                condition: "false"
              - name: last
                run: "echo last"
            """;
        var path = WriteWorkflow("cond_skip.yaml", yaml);

        var response = await _engine.RunAsync(path, [], 10000, CancellationToken.None);

        Assert.Equal("ok", response.Status);

        using var db = _dbFactory.CreateDbContext();
        var run = await db.WorkflowRuns.Include(r => r.StepResults).FirstAsync();
        var skipped = run.StepResults.FirstOrDefault(sr => sr.StepName == "conditional");
        Assert.NotNull(skipped);
        Assert.Equal(StepResultStatus.Skipped, skipped!.Status);
    }

    [Fact]
    public async Task RunAsync_ApprovalGate_PausesAndReturnsNeedsApproval()
    {
        var yaml = """
            name: gated_addl
            steps:
              - name: prepare
                run: "echo preparing"
              - approval:
                  prompt: "Approve deployment?"
                  items: []
              - name: deploy
                run: "echo deploying"
            """;
        var path = WriteWorkflow("gated_addl.yaml", yaml);

        var response = await _engine.RunAsync(path, [], 10000, CancellationToken.None);

        Assert.True(response.Ok);
        Assert.Equal("needs_approval", response.Status);
        Assert.NotNull(response.RequiresApproval);
        Assert.False(string.IsNullOrWhiteSpace(response.RequiresApproval!.ResumeToken));

        using var db = _dbFactory.CreateDbContext();
        var run = await db.WorkflowRuns.Include(r => r.StepResults).FirstAsync();
        Assert.Equal(WorkflowRunStatus.NeedsApproval, run.Status);
        Assert.NotNull(run.ResumeToken);
    }

    [Fact]
    public async Task ResumeAsync_Approved_ContinuesWorkflow()
    {
        var yaml = """
            name: resume_approved
            steps:
              - name: before
                run: "echo before"
              - approval:
                  prompt: "OK to continue?"
                  items: []
              - name: after
                run: "echo after"
            """;
        var path = WriteWorkflow("resume_approved.yaml", yaml);

        var runResponse = await _engine.RunAsync(path, [], 10000, CancellationToken.None);
        Assert.Equal("needs_approval", runResponse.Status);

        var token = runResponse.RequiresApproval!.ResumeToken;
        var resumeResponse = await _engine.ResumeAsync(token, true, CancellationToken.None);

        Assert.True(resumeResponse.Ok);
        Assert.Equal("ok", resumeResponse.Status);

        using var db = _dbFactory.CreateDbContext();
        var run = await db.WorkflowRuns.Include(r => r.StepResults).FirstAsync();
        Assert.Equal(WorkflowRunStatus.Completed, run.Status);
        // before + after (approval step itself produces no StepResult)
        Assert.Equal(2, run.StepResults.Count);
        Assert.All(run.StepResults, sr => Assert.Equal(StepResultStatus.Success, sr.Status));
    }

    [Fact]
    public async Task ResumeAsync_Rejected_CancelledStatus()
    {
        var yaml = """
            name: resume_rejected
            steps:
              - name: init
                run: "echo init"
              - approval:
                  prompt: "Are you sure?"
                  items: []
              - name: final
                run: "echo final"
            """;
        var path = WriteWorkflow("resume_rejected.yaml", yaml);

        var runResponse = await _engine.RunAsync(path, [], 10000, CancellationToken.None);
        Assert.Equal("needs_approval", runResponse.Status);

        var token = runResponse.RequiresApproval!.ResumeToken;
        var resumeResponse = await _engine.ResumeAsync(token, false, CancellationToken.None);

        Assert.False(resumeResponse.Ok);
        Assert.Equal("cancelled", resumeResponse.Status);

        using var db = _dbFactory.CreateDbContext();
        var run = await db.WorkflowRuns.FirstAsync();
        Assert.Equal(WorkflowRunStatus.Cancelled, run.Status);
        Assert.Null(run.ResumeToken);
    }

    [Fact]
    public async Task ResumeAsync_InvalidToken_ReturnsFailure()
    {
        var response = await _engine.ResumeAsync("totally-bogus-token-xyz", true, CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal("error", response.Status);
        Assert.False(string.IsNullOrWhiteSpace(response.Error));
        Assert.Contains("totally-bogus-token-xyz", response.Error);
    }

    [Fact]
    public async Task RunAsync_StepFailure_ReturnsFailureResponse()
    {
        var yaml = """
            name: failing_step
            steps:
              - name: good_step
                run: "echo good"
              - name: bad_step
                run: "exit 1"
              - name: unreachable
                run: "echo should_not_run"
            """;
        var path = WriteWorkflow("failing.yaml", yaml);

        var response = await _engine.RunAsync(path, [], 10000, CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal("error", response.Status);
        Assert.False(string.IsNullOrWhiteSpace(response.Error));

        using var db = _dbFactory.CreateDbContext();
        var run = await db.WorkflowRuns.Include(r => r.StepResults).FirstAsync();
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        var failedStep = run.StepResults.FirstOrDefault(sr => sr.StepName == "bad_step");
        Assert.NotNull(failedStep);
        Assert.Equal(StepResultStatus.Failed, failedStep!.Status);
    }

    [Fact]
    public async Task RunAsync_WithArgs_ResolvedInSteps()
    {
        var yaml = """
            name: args_test
            args:
              - greeting
            steps:
              - name: greet
                run: "echo {{args.greeting}}"
            """;
        var path = WriteWorkflow("args_test.yaml", yaml);
        var args = new Dictionary<string, string> { ["greeting"] = "hello" };

        var response = await _engine.RunAsync(path, args, 10000, CancellationToken.None);

        Assert.True(response.Ok);
        Assert.Equal("ok", response.Status);

        using var db = _dbFactory.CreateDbContext();
        var run = await db.WorkflowRuns.Include(r => r.StepResults).FirstAsync();
        Assert.Equal(WorkflowRunStatus.Completed, run.Status);
        var greetStep = run.StepResults.FirstOrDefault(sr => sr.StepName == "greet");
        Assert.NotNull(greetStep);
        Assert.Contains("hello", greetStep!.Stdout);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string WriteWorkflow(string filename, string yaml)
    {
        var path = Path.Combine(_workflowDir, filename);
        File.WriteAllText(path, yaml);
        return path;
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* best-effort */ }
        try { Directory.Delete(_workflowDir, recursive: true); } catch { /* best-effort */ }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Test helper — local copy to avoid cross-file type conflicts
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class AdditionalTestDbContextFactory(DbContextOptions<WorkflowyDbContext> options)
    : IDbContextFactory<WorkflowyDbContext>
{
    public WorkflowyDbContext CreateDbContext() => new(options);
    public Task<WorkflowyDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(new WorkflowyDbContext(options));
}
