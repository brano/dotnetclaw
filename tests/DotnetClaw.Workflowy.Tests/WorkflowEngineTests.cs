using DotnetClaw.Workflowy.Config;
using DotnetClaw.Workflowy.Data;
using DotnetClaw.Workflowy.Engine;
using DotnetClaw.Workflowy.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotnetClaw.Workflowy.Tests;

/// <summary>
/// Integration tests for WorkflowEngine using a real SQLite DB in a temp file.
/// </summary>
public sealed class WorkflowEngineTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IDbContextFactory<WorkflowyDbContext> _dbFactory;
    private readonly WorkflowEngine _engine;
    private readonly WorkflowLoader _loader = new();
    private readonly VariableResolver _resolver = new();
    private readonly string _workflowDir;

    public WorkflowEngineTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"workflowy_test_{Guid.NewGuid():N}.db");
        _workflowDir = Path.Combine(Path.GetTempPath(), $"wf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workflowDir);

        var opts = new DbContextOptionsBuilder<WorkflowyDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        _dbFactory = new TestDbContextFactory(opts);

        // Ensure schema
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

    [Fact]
    public async Task RunAsync_SimpleWorkflow_ReturnsOk()
    {
        var yaml = """
            name: simple
            steps:
              - name: greet
                run: "echo hello"
            """;
        var path = WriteWorkflow("simple.yaml", yaml);

        var response = await _engine.RunAsync(path, [], 5000, CancellationToken.None);

        Assert.True(response.Ok);
        Assert.Equal("ok", response.Status);

        // Verify DB
        using var db = _dbFactory.CreateDbContext();
        var run = await db.WorkflowRuns.Include(r => r.StepResults).FirstAsync();
        Assert.Equal(WorkflowRunStatus.Completed, run.Status);
        Assert.Single(run.StepResults);
        Assert.Equal(StepResultStatus.Success, run.StepResults[0].Status);
    }

    [Fact]
    public async Task RunAsync_ApprovalGate_ReturnsneedsApproval()
    {
        var yaml = """
            name: gated
            steps:
              - name: prepare
                run: "echo prepared"
              - approval:
                  prompt: "Continue?"
                  items: []
              - name: finish
                run: "echo finished"
            """;
        var path = WriteWorkflow("gated.yaml", yaml);

        var response = await _engine.RunAsync(path, [], 5000, CancellationToken.None);

        Assert.True(response.Ok);
        Assert.Equal("needs_approval", response.Status);
        Assert.NotNull(response.RequiresApproval);
        Assert.False(string.IsNullOrEmpty(response.RequiresApproval!.ResumeToken));

        // Check DB state
        using var db = _dbFactory.CreateDbContext();
        var run = await db.WorkflowRuns.Include(r => r.StepResults).FirstAsync();
        Assert.Equal(WorkflowRunStatus.NeedsApproval, run.Status);
        Assert.NotNull(run.ResumeToken);
    }

    [Fact]
    public async Task ResumeAsync_Approve_CompletesWorkflow()
    {
        var yaml = """
            name: resume_test
            steps:
              - name: step1
                run: "echo step1"
              - approval:
                  prompt: "OK?"
                  items: []
              - name: step2
                run: "echo step2"
            """;
        var path = WriteWorkflow("resume.yaml", yaml);

        var runResponse = await _engine.RunAsync(path, [], 5000, CancellationToken.None);
        Assert.Equal("needs_approval", runResponse.Status);

        var token = runResponse.RequiresApproval!.ResumeToken;
        var resumeResponse = await _engine.ResumeAsync(token, true, CancellationToken.None);

        Assert.True(resumeResponse.Ok);
        Assert.Equal("ok", resumeResponse.Status);

        using var db = _dbFactory.CreateDbContext();
        var run = await db.WorkflowRuns.Include(r => r.StepResults).FirstAsync();
        Assert.Equal(WorkflowRunStatus.Completed, run.Status);
        // step1 + step2 results (approval step has no StepResult record, it's just a gate)
        Assert.Equal(2, run.StepResults.Count);
    }

    [Fact]
    public async Task ResumeAsync_Reject_CancelsWorkflow()
    {
        var yaml = """
            name: cancel_test
            steps:
              - name: step1
                run: "echo step1"
              - approval:
                  prompt: "Cancel me?"
                  items: []
              - name: step2
                run: "echo step2"
            """;
        var path = WriteWorkflow("cancel.yaml", yaml);

        var runResponse = await _engine.RunAsync(path, [], 5000, CancellationToken.None);
        var token = runResponse.RequiresApproval!.ResumeToken;

        var cancelResponse = await _engine.ResumeAsync(token, false, CancellationToken.None);

        Assert.False(cancelResponse.Ok);
        Assert.Equal("cancelled", cancelResponse.Status);

        using var db = _dbFactory.CreateDbContext();
        var run = await db.WorkflowRuns.FirstAsync();
        Assert.Equal(WorkflowRunStatus.Cancelled, run.Status);
        Assert.Null(run.ResumeToken);
    }

    [Fact]
    public async Task RunAsync_ConditionFalse_SkipsStep()
    {
        var yaml = """
            name: cond_test
            steps:
              - name: first
                run: "echo first"
              - name: skipped
                run: "echo skipped"
                condition: "{{first.exitCode}} == 99"
              - name: last
                run: "echo last"
            """;
        var path = WriteWorkflow("cond.yaml", yaml);
        var response = await _engine.RunAsync(path, [], 5000, CancellationToken.None);

        Assert.Equal("ok", response.Status);
        using var db = _dbFactory.CreateDbContext();
        var run = await db.WorkflowRuns.Include(r => r.StepResults).FirstAsync();
        var skipped = run.StepResults.FirstOrDefault(r => r.StepName == "skipped");
        Assert.NotNull(skipped);
        Assert.Equal(StepResultStatus.Skipped, skipped!.Status);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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
// Test helper — synchronous factory wrapping pre-built options
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class TestDbContextFactory(DbContextOptions<WorkflowyDbContext> options)
    : IDbContextFactory<WorkflowyDbContext>
{
    public WorkflowyDbContext CreateDbContext() => new(options);
}
