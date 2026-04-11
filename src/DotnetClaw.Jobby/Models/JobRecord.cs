using System.Text.Json.Serialization;

namespace DotnetClaw.Jobby.Models;

/// <summary>
/// Persisted representation of a scheduled background job.
/// Stored as JSON under ~/.dotnetclaw/cron/jobs.json
/// </summary>
public sealed class JobRecord
{
    /// <summary>Short random ID, e.g. "a3f7b2c1".</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Human-friendly label shown in listings.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The prompt that will be sent to the agent when this job fires.</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>Recurring (CRON expression) or OneShot (absolute run time).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public JobType Type { get; set; } = JobType.Recurring;

    /// <summary>
    /// Standard 5-part CRON expression for recurring jobs (e.g. "0 8 * * *").
    /// Null for OneShot jobs.
    /// </summary>
    public string? CronExpression { get; set; }

    /// <summary>UTC time to execute a one-shot job. Null for recurring jobs.</summary>
    public DateTimeOffset? RunAt { get; set; }

    /// <summary>
    /// When true the job executes in its own isolated agent session (fresh context).
    /// When false it runs in the shared main session.
    /// </summary>
    public bool IsolatedSession { get; set; } = true;

    public bool Enabled { get; set; } = true;

    /// <summary>Pre-computed UTC time of the next scheduled execution.</summary>
    public DateTimeOffset? NextRunAt { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>Trimmed first 500 chars of the agent's response from the last run.</summary>
    public string? LastResult { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum JobType
{
    Recurring,
    OneShot,
}
