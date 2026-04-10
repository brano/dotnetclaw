namespace DotnetClaw.Workflowy.Models;

// ============================================================================
//  EF Core entity — persisted to SQLite
// ============================================================================

public enum WorkflowRunStatus
{
    Running,
    NeedsApproval,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>Persisted record of a single workflow execution instance.</summary>
public sealed class WorkflowRun
{
    public int Id { get; set; }
    public string WorkflowName { get; set; } = string.Empty;
    public string WorkflowPath { get; set; } = string.Empty;
    public WorkflowRunStatus Status { get; set; }

    /// <summary>Serialized Dictionary&lt;string,string&gt; of args supplied at invocation.</summary>
    public string ArgsJson { get; set; } = "{}";

    /// <summary>Index of the next step to execute. Used as resume pointer.</summary>
    public int NextStepIndex { get; set; }

    /// <summary>Serialized interpolation context (accumulated step outputs, args, env).</summary>
    public string ContextJson { get; set; } = "{}";

    /// <summary>Opaque URL-safe base64 GUID resume token. Set when Status=NeedsApproval.</summary>
    public string? ResumeToken { get; set; }

    /// <summary>Serialized PendingApprovalDto. Set when Status=NeedsApproval.</summary>
    public string? PendingApprovalJson { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public List<StepResult> StepResults { get; set; } = [];
}
