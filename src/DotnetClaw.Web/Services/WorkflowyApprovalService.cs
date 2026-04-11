using DotnetClaw.Workflowy.Engine;
using DotnetClaw.Workflowy.Plugin;

namespace DotnetClaw.Web.Services;

/// <summary>
/// Singleton service that:
///  - Implements <see cref="IApprovalNotifier"/> so WorkflowyPlugin can push approval events into the web UI.
///  - Maintains the in-memory list of pending human tasks.
///  - Exposes <see cref="ApproveAsync"/> so the /tasks page can approve or reject without going through the agent.
/// </summary>
public sealed class WorkflowyApprovalService(
    WorkflowEngine engine,
    ILogger<WorkflowyApprovalService> logger) : IApprovalNotifier
{
    private readonly List<PendingApprovalDto> _tasks = [];
    private readonly Lock _lock = new();

    /// <summary>Fired whenever the pending task list changes (added or removed).</summary>
    public event Action? OnTasksChanged;

    /// <summary>Snapshot of all currently pending approval requests.</summary>
    public IReadOnlyList<PendingApprovalDto> PendingTasks
    {
        get { lock (_lock) return [.._tasks]; }
    }

    // ── IApprovalNotifier ────────────────────────────────────────────────────

    public Task NotifyPendingAsync(PendingApprovalDto approval, CancellationToken ct = default)
    {
        lock (_lock) _tasks.Add(approval);
        logger.LogInformation("Workflowy approval pending: {Token} — {Prompt}", approval.Token, approval.Prompt);
        OnTasksChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task NotifyResumedAsync(string token, string status, CancellationToken ct = default)
    {
        int removed;
        lock (_lock) removed = _tasks.RemoveAll(t => t.Token == token);
        if (removed > 0) OnTasksChanged?.Invoke();
        return Task.CompletedTask;
    }

    // ── Web UI action ────────────────────────────────────────────────────────

    /// <summary>
    /// Called by the Tasks page when the user clicks Approve or Reject.
    /// Resumes the workflow engine directly and removes the task from the pending list.
    /// </summary>
    public async Task<WorkflowyResponse> ApproveAsync(string token, bool approved, CancellationToken ct = default)
    {
        logger.LogInformation("User {Action} workflow token {Token}", approved ? "approved" : "rejected", token);
        var response = await engine.ResumeAsync(token, approved, ct);
        await NotifyResumedAsync(token, response.Status, ct);
        return response;
    }
}
