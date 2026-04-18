using DotnetClaw.Workflowy.Plugin;

namespace DotnetClaw.Workflowy.Engine;

/// <summary>
/// Abstraction over <see cref="WorkflowEngine"/> to enable unit testing of
/// <c>WorkflowyPlugin</c> without a real database (Moq cannot mock sealed classes).
/// </summary>
public interface IWorkflowEngine
{
    /// <summary>
    /// Starts a new workflow run from a file path.
    /// Returns a response with status "ok", "needs_approval", or "error".
    /// </summary>
    Task<WorkflowyResponse> RunAsync(
        string workflowPath,
        Dictionary<string, string> args,
        int timeoutMs,
        CancellationToken ct);

    /// <summary>
    /// Resumes a workflow paused at an approval gate.
    /// Pass <paramref name="approved"/>=true to continue, false to cancel.
    /// </summary>
    Task<WorkflowyResponse> ResumeAsync(
        string resumeToken,
        bool approved,
        CancellationToken ct);
}
