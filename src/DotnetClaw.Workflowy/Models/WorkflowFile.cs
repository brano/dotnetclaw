namespace DotnetClaw.Workflowy.Models;

// ============================================================================
//  Parse-time models (not persisted to DB)
// ============================================================================

/// <summary>A parsed workflow definition loaded from a YAML or JSON file.</summary>
public sealed class WorkflowFile
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Named arguments declared by this workflow (e.g. ["mailbox", "limit"]).</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>Environment variables merged into the interpolation context as env.*.</summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>Ordered list of steps to execute.</summary>
    public List<WorkflowStep> Steps { get; set; } = [];
}

/// <summary>A single workflow step. Exactly one of Run/Command, Pipeline, or Approval must be set.</summary>
public sealed class WorkflowStep
{
    /// <summary>Optional step name used for variable references: {{stepname.stdout}}.</summary>
    public string? Name { get; set; }

    /// <summary>Condition expression. If false, step is skipped. Supports {{token}} == value comparisons.</summary>
    public string? Condition { get; set; }

    /// <summary>Shell command to execute. Supports {{variable}} interpolation.</summary>
    public string? Run { get; set; }

    /// <summary>Synonym for Run (either field is accepted in YAML/JSON).</summary>
    public string? Command { get; set; }

    /// <summary>Native Workflowy pipeline directive (e.g. "llm.invoke --prompt '...'").</summary>
    public string? Pipeline { get; set; }

    /// <summary>Human approval gate. Pauses the workflow and requires explicit approval to continue.</summary>
    public ApprovalBlock? Approval { get; set; }

    /// <summary>Returns the effective shell command (Run takes precedence over Command).</summary>
    public string? EffectiveRun => Run ?? Command;
}

/// <summary>Configuration for a human approval gate step.</summary>
public sealed class ApprovalBlock
{
    /// <summary>Question or description shown to the human approver.</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>List of items (e.g. previews) to show alongside the prompt.</summary>
    public List<string> Items { get; set; } = [];
}
