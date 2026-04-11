using System.ComponentModel;
using System.Text;
using Cronos;
using DotnetClaw.Jobby.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace DotnetClaw.Jobby;

/// <summary>
/// Semantic Kernel plugin that lets the agent manage background jobs.
///
/// The agent can call these functions in response to natural-language requests like:
///   "send me a summary of my calendar every morning at 8:00"
///   → schedule_job(name, "summarise my calendar", "0 8 * * *")
///
/// Natural language schedules are converted to CRON expressions by the LLM
/// before passing them to <see cref="ScheduleJobAsync"/>.
/// </summary>
public sealed class JobbyPlugin(
    CronStore store,
    IJobExecutor executor,
    ILogger<JobbyPlugin> logger)
{
    // ── Schedule recurring job ────────────────────────────────────────────────

    [KernelFunction("schedule_job")]
    [Description(
        "Schedule a recurring background job using a CRON expression. " +
        "The job will run the given prompt through the agent on the specified schedule. " +
        "When the user describes a schedule in natural language (e.g. 'every morning at 8:00', " +
        "'every Monday at noon', 'twice a day') convert it to a standard 5-part CRON expression " +
        "(minute hour dom month dow) before calling this function. " +
        "Returns the new job ID.")]
    public async Task<string> ScheduleJobAsync(
        [Description("Human-readable name for this job, e.g. 'Morning calendar summary'")]
        string name,
        [Description("The prompt / task to run, e.g. 'Summarise my Google Calendar events for today and send via Telegram'")]
        string prompt,
        [Description("Standard 5-part CRON expression, e.g. '0 8 * * *' for daily at 08:00 UTC")]
        string cronExpression,
        [Description("When true (default) the job runs in an isolated session so it does not affect the main conversation")]
        bool isolatedSession = true,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseCron(cronExpression, out var parsed))
            return $"Invalid CRON expression '{cronExpression}'. Use standard 5-part format: minute hour dom month dow.";

        var job = new JobRecord
        {
            Name = name,
            Prompt = prompt,
            Type = JobType.Recurring,
            CronExpression = cronExpression,
            IsolatedSession = isolatedSession,
            Enabled = true,
        };

        job.NextRunAt = CronService.ComputeNextRun(cronExpression, DateTimeOffset.UtcNow);

        await store.SaveAsync(job, cancellationToken);
        logger.LogInformation("Scheduled recurring job {Id} ({Name}) cron={Cron}", job.Id, job.Name, cronExpression);

        return $"Job scheduled. ID: {job.Id}  Name: {job.Name}  Next run: {job.NextRunAt:u}";
    }

    // ── Schedule one-shot job ─────────────────────────────────────────────────

    [KernelFunction("schedule_once")]
    [Description(
        "Schedule a one-shot background job that runs the prompt once at a specific UTC time. " +
        "Returns the new job ID.")]
    public async Task<string> ScheduleOnceAsync(
        [Description("Human-readable name for this job")]
        string name,
        [Description("The prompt / task to run")]
        string prompt,
        [Description("UTC date-time to run the job, ISO 8601 format, e.g. '2025-04-11T08:00:00Z'")]
        DateTimeOffset runAt,
        [Description("When true (default) the job runs in an isolated session")]
        bool isolatedSession = true,
        CancellationToken cancellationToken = default)
    {
        if (runAt <= DateTimeOffset.UtcNow)
            return $"runAt must be in the future (got {runAt:u}, now is {DateTimeOffset.UtcNow:u}).";

        var job = new JobRecord
        {
            Name = name,
            Prompt = prompt,
            Type = JobType.OneShot,
            RunAt = runAt,
            NextRunAt = runAt,
            IsolatedSession = isolatedSession,
            Enabled = true,
        };

        await store.SaveAsync(job, cancellationToken);
        logger.LogInformation("Scheduled one-shot job {Id} ({Name}) at={RunAt}", job.Id, job.Name, runAt);

        return $"One-shot job scheduled. ID: {job.Id}  Name: {job.Name}  Runs at: {runAt:u}";
    }

    // ── List jobs ─────────────────────────────────────────────────────────────

    [KernelFunction("list_jobs")]
    [Description("List all scheduled background jobs (enabled and disabled).")]
    public async Task<string> ListJobsAsync(CancellationToken cancellationToken = default)
    {
        var jobs = await store.LoadAllAsync(cancellationToken);

        if (jobs.Count == 0)
            return "No jobs scheduled.";

        var sb = new StringBuilder();
        sb.AppendLine($"{"ID",-10} {"Name",-30} {"Type",-10} {"Schedule",-15} {"Enabled",-8} {"Next Run",-22} Last Run");
        sb.AppendLine(new string('─', 110));

        foreach (var j in jobs)
        {
            var schedule = j.Type == JobType.Recurring ? j.CronExpression : j.RunAt?.ToString("u");
            var next = j.NextRunAt?.ToString("u") ?? "—";
            var last = j.LastRunAt?.ToString("u") ?? "—";
            sb.AppendLine($"{j.Id,-10} {j.Name[..Math.Min(30, j.Name.Length)],-30} {j.Type,-10} {schedule,-15} {(j.Enabled ? "yes" : "no"),-8} {next,-22} {last}");
        }

        return sb.ToString();
    }

    // ── Get job details ───────────────────────────────────────────────────────

    [KernelFunction("get_job")]
    [Description("Get full details and the last result for a specific job by its ID.")]
    public async Task<string> GetJobAsync(
        [Description("The 8-character job ID returned by schedule_job or list_jobs")]
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await store.GetByIdAsync(jobId, cancellationToken);
        if (job is null) return $"Job '{jobId}' not found.";

        return $"""
                ID:              {job.Id}
                Name:            {job.Name}
                Type:            {job.Type}
                Schedule:        {(job.Type == JobType.Recurring ? job.CronExpression : job.RunAt?.ToString("u"))}
                Prompt:          {job.Prompt}
                Isolated:        {job.IsolatedSession}
                Enabled:         {job.Enabled}
                Next run:        {job.NextRunAt?.ToString("u") ?? "—"}
                Last run:        {job.LastRunAt?.ToString("u") ?? "—"}
                Last result:     {job.LastResult ?? "—"}
                Created:         {job.CreatedAt:u}
                """;
    }

    // ── Delete job ────────────────────────────────────────────────────────────

    [KernelFunction("delete_job")]
    [Description("Permanently delete a scheduled job by its ID.")]
    public async Task<string> DeleteJobAsync(
        [Description("The 8-character job ID")]
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var deleted = await store.DeleteAsync(jobId, cancellationToken);
        return deleted ? $"Job {jobId} deleted." : $"Job '{jobId}' not found.";
    }

    // ── Enable / disable ──────────────────────────────────────────────────────

    [KernelFunction("enable_job")]
    [Description("Re-enable a previously disabled job.")]
    public async Task<string> EnableJobAsync(
        [Description("The 8-character job ID")]
        string jobId,
        CancellationToken cancellationToken = default)
        => await SetEnabledAsync(jobId, true, cancellationToken);

    [KernelFunction("disable_job")]
    [Description("Pause a job without deleting it. It can be re-enabled later.")]
    public async Task<string> DisableJobAsync(
        [Description("The 8-character job ID")]
        string jobId,
        CancellationToken cancellationToken = default)
        => await SetEnabledAsync(jobId, false, cancellationToken);

    // ── Run now ───────────────────────────────────────────────────────────────

    [KernelFunction("run_job_now")]
    [Description("Execute a job immediately, regardless of its schedule.")]
    public async Task<string> RunJobNowAsync(
        [Description("The 8-character job ID")]
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await store.GetByIdAsync(jobId, cancellationToken);
        if (job is null) return $"Job '{jobId}' not found.";

        logger.LogInformation("Running job {Id} ({Name}) on demand", job.Id, job.Name);

        string result;
        try
        {
            result = await executor.ExecuteAsync(job.Prompt, job.IsolatedSession, cancellationToken);
        }
        catch (Exception ex)
        {
            result = $"[ERROR] {ex.Message}";
        }

        job.LastRunAt = DateTimeOffset.UtcNow;
        job.LastResult = result.Length > 500 ? result[..500] + "…" : result;
        await store.SaveAsync(job, cancellationToken);

        return $"Job executed.\n\nResult:\n{result}";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> SetEnabledAsync(string jobId, bool enabled, CancellationToken ct)
    {
        var job = await store.GetByIdAsync(jobId, ct);
        if (job is null) return $"Job '{jobId}' not found.";

        job.Enabled = enabled;

        // Recompute NextRunAt when re-enabling a recurring job
        if (enabled && job.Type == JobType.Recurring && job.CronExpression is not null)
            job.NextRunAt = CronService.ComputeNextRun(job.CronExpression, DateTimeOffset.UtcNow);

        await store.SaveAsync(job, ct);
        return $"Job {jobId} {(enabled ? "enabled" : "disabled")}. Next run: {job.NextRunAt?.ToString("u") ?? "—"}";
    }

    private static bool TryParseCron(string expression, out CronExpression? result)
    {
        try
        {
            result = CronExpression.Parse(expression, CronFormat.Standard);
            return true;
        }
        catch
        {
            result = null;
            return false;
        }
    }
}
