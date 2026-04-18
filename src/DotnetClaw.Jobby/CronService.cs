using Cronos;
using DotnetClaw.Jobby.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetClaw.Jobby;

/// <summary>
/// Background service that drives the job scheduler.
///
/// Design:
///   • Polls the <see cref="CronStore"/> every <see cref="TickInterval"/> (30 s)
///   • For each enabled job whose <c>NextRunAt</c> ≤ UTC now it:
///       1. Calls <see cref="IJobExecutor.ExecuteAsync"/> with the job prompt
///       2. Records <c>LastRunAt</c> and a trimmed <c>LastResult</c>
///       3. For recurring jobs: advances <c>NextRunAt</c> using the CRON expression
///       4. For one-shot jobs: disables the job so it never fires again
///   • Jobs are executed sequentially per tick (fire-and-forget across ticks)
/// </summary>
public sealed class CronService(
    ICronStore store,
    IJobExecutor executor,
    ILogger<CronService> logger) : IHostedService, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private CancellationTokenSource? _cts;
    private Task? _loop;

    // ── IHostedService ────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
        logger.LogInformation("CronService started. Tick interval: {Interval}s", TickInterval.TotalSeconds);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("CronService stopping…");
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        if (_loop is not null)
        {
            try { await _loop.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { /* expected */ }
        }
    }

    public void Dispose() => _cts?.Dispose();

    // ── Main loop ─────────────────────────────────────────────────────────────

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in CronService tick. Continuing…");
            }

            await Task.Delay(TickInterval, ct);
        }

        logger.LogInformation("CronService loop stopped.");
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var jobs = await store.LoadAllAsync(ct);
        var due = jobs.Where(j => j.Enabled && j.NextRunAt.HasValue && j.NextRunAt <= now).ToList();

        if (due.Count == 0)
            return;

        logger.LogInformation("CronService tick: {Count} job(s) due at {Now}", due.Count, now);

        foreach (var job in due)
        {
            if (ct.IsCancellationRequested)
                break;

            await ExecuteJobAsync(job, ct);
        }
    }

    // ── Job execution ─────────────────────────────────────────────────────────

    private async Task ExecuteJobAsync(JobRecord job, CancellationToken ct)
    {
        logger.LogInformation("Executing job {Id} ({Name}): {Prompt}", job.Id, job.Name, job.Prompt[..Math.Min(80, job.Prompt.Length)]);

        string result;
        try
        {
            result = await executor.ExecuteAsync(job.Prompt, job.IsolatedSession, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {Id} ({Name}) failed", job.Id, job.Name);
            result = $"[ERROR] {ex.Message}";
        }

        job.LastRunAt = DateTimeOffset.UtcNow;
        job.LastResult = result.Length > 500 ? result[..500] + "…" : result;

        if (job.Type == JobType.Recurring && job.CronExpression is not null)
        {
            job.NextRunAt = ComputeNextRun(job.CronExpression, job.LastRunAt.Value);
            logger.LogInformation("Job {Id} next run: {Next}", job.Id, job.NextRunAt);
        }
        else
        {
            // One-shot: disable after firing
            job.Enabled = false;
            job.NextRunAt = null;
            logger.LogInformation("One-shot job {Id} completed and disabled.", job.Id);
        }

        await store.SaveAsync(job, ct);
    }

    // ── CRON helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Compute the next UTC run time after <paramref name="after"/> using Cronos.
    /// Returns null if the expression never fires again.
    /// </summary>
    internal static DateTimeOffset? ComputeNextRun(string cronExpression, DateTimeOffset after)
    {
        try
        {
            var expr = CronExpression.Parse(cronExpression, CronFormat.Standard);
            var next = expr.GetNextOccurrence(after.UtcDateTime, TimeZoneInfo.Utc);
            return next.HasValue ? new DateTimeOffset(next.Value, TimeSpan.Zero) : null;
        }
        catch
        {
            return null;
        }
    }
}
