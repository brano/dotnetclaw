using System.Text.Json;
using DotnetClaw.Workflowy.Data;
using DotnetClaw.Workflowy.Models;
using DotnetClaw.Workflowy.Plugin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotnetClaw.Workflowy.Engine;

/// <summary>
/// Orchestrates workflow execution: loads the file, runs steps sequentially,
/// handles approval gates (pause/resume), and persists all state to SQLite.
/// </summary>
public sealed class WorkflowEngine(
    IDbContextFactory<WorkflowyDbContext> dbFactory,
    WorkflowLoader loader,
    StepExecutor executor,
    PipelineDispatcher dispatcher,
    VariableResolver resolver,
    ILogger<WorkflowEngine> logger) : IWorkflowEngine
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>Generates a URL-safe base64-encoded GUID as a compact, opaque resume token.</summary>
    public static string GenerateToken() =>
        Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a new workflow run from a file path and returns a response envelope.
    /// The response status is "ok", "needs_approval", or "error".
    /// </summary>
    public async Task<WorkflowyResponse> RunAsync(
        string workflowPath,
        Dictionary<string, string> args,
        int timeoutMs,
        CancellationToken ct)
    {
        WorkflowFile workflow;
        try
        {
            workflow = loader.Load(workflowPath);
            var errors = loader.Validate(workflow);
            if (errors.Count > 0)
                return WorkflowyResponse.Failure(string.Join("; ", errors));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load workflow: {Path}", workflowPath);
            return WorkflowyResponse.Failure($"Failed to load workflow: {ex.Message}");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var run = new WorkflowRun
        {
            WorkflowName = workflow.Name,
            WorkflowPath = Path.GetFullPath(workflowPath),
            Status = WorkflowRunStatus.Running,
            ArgsJson = JsonSerializer.Serialize(args, JsonOpts),
            StartedAt = DateTimeOffset.UtcNow,
        };
        db.WorkflowRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var context = resolver.BuildInitialContext(workflow, args);
        return await ExecuteFromStepAsync(db, run, workflow, context, ct);
    }

    /// <summary>
    /// Resumes a workflow run that is awaiting human approval.
    /// Pass <paramref name="approved"/>=true to continue, false to cancel.
    /// </summary>
    public async Task<WorkflowyResponse> ResumeAsync(
        string resumeToken,
        bool approved,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await db.WorkflowRuns
            .Include(r => r.StepResults)
            .FirstOrDefaultAsync(r => r.ResumeToken == resumeToken, ct);

        if (run is null)
            return WorkflowyResponse.Failure($"No workflow run found for token '{resumeToken}'.");

        if (run.Status != WorkflowRunStatus.NeedsApproval)
            return WorkflowyResponse.Failure(
                $"Workflow run {run.Id} is not awaiting approval (current status: {run.Status}).");

        if (!approved)
        {
            run.Status = WorkflowRunStatus.Cancelled;
            run.ResumeToken = null;
            run.PendingApprovalJson = null;
            run.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Workflow run {Id} cancelled by user.", run.Id);
            return new WorkflowyResponse(false, "cancelled");
        }

        // Restore context and advance past the approval step
        var context = JsonSerializer.Deserialize<Dictionary<string, string>>(run.ContextJson, JsonOpts) ?? [];
        run.ResumeToken = null;
        run.PendingApprovalJson = null;
        run.Status = WorkflowRunStatus.Running;
        run.NextStepIndex++;   // skip the approval step itself
        await db.SaveChangesAsync(ct);

        WorkflowFile workflow;
        try
        {
            workflow = loader.Load(run.WorkflowPath);
        }
        catch (Exception ex)
        {
            run.Status = WorkflowRunStatus.Failed;
            run.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            return WorkflowyResponse.Failure($"Failed to reload workflow for resume: {ex.Message}");
        }

        return await ExecuteFromStepAsync(db, run, workflow, context, ct);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private execution loop
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<WorkflowyResponse> ExecuteFromStepAsync(
        WorkflowyDbContext db,
        WorkflowRun run,
        WorkflowFile workflow,
        Dictionary<string, string> context,
        CancellationToken ct)
    {
        var outputs = new List<string>();

        for (var i = run.NextStepIndex; i < workflow.Steps.Count; i++)
        {
            run.NextStepIndex = i;
            var step = workflow.Steps[i];
            var stepName = step.Name ?? $"step_{i}";

            // ── Condition check ──────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(step.Condition))
            {
                if (!resolver.EvaluateCondition(step.Condition, context))
                {
                    logger.LogInformation("Skipping [{Name}] — condition not met: {Cond}", stepName, step.Condition);
                    var skip = new StepResult
                    {
                        WorkflowRunId = run.Id, StepIndex = i, StepName = stepName,
                        StepType = "skipped", Status = StepResultStatus.Skipped,
                        StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
                    };
                    db.StepResults.Add(skip);
                    await db.SaveChangesAsync(ct);
                    continue;
                }
            }

            // ── Approval gate ────────────────────────────────────────────────
            if (step.Approval is not null)
            {
                var token = GenerateToken();
                var resolvedPrompt = resolver.Resolve(step.Approval.Prompt, context);
                var resolvedItems = step.Approval.Items
                    .Select(item => resolver.Resolve(item, context))
                    .ToList();

                var pendingDto = new PendingApprovalDto(
                    token, workflow.Name, resolvedPrompt, resolvedItems, DateTimeOffset.UtcNow);

                run.Status = WorkflowRunStatus.NeedsApproval;
                run.ResumeToken = token;
                run.ContextJson = JsonSerializer.Serialize(context, JsonOpts);
                run.NextStepIndex = i;
                run.PendingApprovalJson = JsonSerializer.Serialize(pendingDto, JsonOpts);
                await db.SaveChangesAsync(ct);

                logger.LogInformation(
                    "Workflow run {Id} paused at approval [{Step}]. Token: {Token}",
                    run.Id, stepName, token);

                return new WorkflowyResponse(true, "needs_approval")
                {
                    Output = outputs.Count > 0 ? [new OutputItem(string.Join("\n", outputs))] : null,
                    RequiresApproval = new ApprovalRequest(
                        "approval_request", resolvedPrompt, resolvedItems, token),
                };
            }

            // ── Run: step ────────────────────────────────────────────────────
            StepResult result;
            if (step.EffectiveRun is not null)
            {
                result = await executor.ExecuteAsync(step, i, context, ct);
            }
            else if (step.Pipeline is not null)
            {
                var resolvedPipeline = resolver.Resolve(step.Pipeline, context);
                result = await dispatcher.DispatchAsync(resolvedPipeline, i, stepName, context, ct);
            }
            else
            {
                result = new StepResult
                {
                    StepIndex = i, StepName = stepName, StepType = "unknown",
                    Status = StepResultStatus.Failed,
                    Stderr = "No action defined for this step (should have been caught by Validate).",
                    StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
                };
            }

            result.WorkflowRunId = run.Id;
            db.StepResults.Add(result);
            await db.SaveChangesAsync(ct);

            resolver.AddStepOutputs(context, stepName, result);

            if (!string.IsNullOrWhiteSpace(result.Stdout))
                outputs.Add(result.Stdout);

            if (result.Status is StepResultStatus.Failed or StepResultStatus.TimedOut)
            {
                run.Status = WorkflowRunStatus.Failed;
                run.CompletedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                logger.LogWarning("Workflow run {Id} failed at step [{Step}].", run.Id, stepName);
                return WorkflowyResponse.Failure(
                    $"Step '{stepName}' {result.Status.ToString().ToLower()} " +
                    $"(exit {result.ExitCode}). Stderr: {result.Stderr}");
            }
        }

        // ── All steps completed ──────────────────────────────────────────────
        run.Status = WorkflowRunStatus.Completed;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Workflow run {Id} completed.", run.Id);

        return new WorkflowyResponse(true, "ok")
        {
            Output = outputs.Count > 0
                ? [new OutputItem(string.Join("\n", outputs))]
                : [],
        };
    }
}
