namespace DotnetClaw.Workflowy.Plugin;

/// <summary>
/// Hook for notifying external surfaces (e.g. Web UI) when a workflow pauses for approval
/// or resumes after an approval decision.
///
/// In DotnetClaw CLI: registered as <see cref="NoOpApprovalNotifier"/> (does nothing).
/// In DotnetClaw.Web: registered as <c>WorkflowyApprovalService</c> which updates the /tasks UI.
/// </summary>
public interface IApprovalNotifier
{
    Task NotifyPendingAsync(PendingApprovalDto approval, CancellationToken ct = default);
    Task NotifyResumedAsync(string token, string status, CancellationToken ct = default);
}

/// <summary>No-op implementation used in the CLI where no web UI notification is needed.</summary>
public sealed class NoOpApprovalNotifier : IApprovalNotifier
{
    public Task NotifyPendingAsync(PendingApprovalDto approval, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task NotifyResumedAsync(string token, string status, CancellationToken ct = default)
        => Task.CompletedTask;
}
