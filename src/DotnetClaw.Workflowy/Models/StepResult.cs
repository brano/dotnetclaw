namespace DotnetClaw.Workflowy.Models;

// ============================================================================
//  EF Core entity — persisted to SQLite
// ============================================================================

public enum StepResultStatus
{
    Success,
    Failed,
    Skipped,
    TimedOut,
}

/// <summary>Persisted record of a single step execution within a workflow run.</summary>
public sealed class StepResult
{
    public int Id { get; set; }
    public int WorkflowRunId { get; set; }
    public WorkflowRun WorkflowRun { get; set; } = null!;

    public int StepIndex { get; set; }
    public string StepName { get; set; } = string.Empty;

    /// <summary>Step type: "run", "pipeline", "approval", "skipped".</summary>
    public string StepType { get; set; } = string.Empty;

    public StepResultStatus Status { get; set; }

    /// <summary>Captured stdout (capped at WorkflowyOptions.StepOutputCaptureLimitBytes).</summary>
    public string Stdout { get; set; } = string.Empty;

    /// <summary>Captured stderr (capped at WorkflowyOptions.StepOutputCaptureLimitBytes).</summary>
    public string Stderr { get; set; } = string.Empty;

    public int ExitCode { get; set; }

    /// <summary>True if output was truncated due to the capture limit.</summary>
    public bool WasTruncated { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}
