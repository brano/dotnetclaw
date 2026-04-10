using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetClaw.Jobby.Models;
using Microsoft.Extensions.Logging;

namespace DotnetClaw.Jobby;

/// <summary>
/// Reads and writes the job list from/to a JSON file under
/// <c>~/.dotnetclaw/cron/jobs.json</c>.
///
/// Thread-safety: all public methods are async and serialise access with a
/// <see cref="SemaphoreSlim"/> so the CronService and plugin can both write safely.
/// </summary>
public sealed class CronStore(ILogger<CronStore> logger)
{
    private static readonly string StoreDir =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnetclaw", "cron");

    private static readonly string StoreFile = Path.Combine(StoreDir, "jobs.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _lock = new(1, 1);

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<List<JobRecord>> LoadAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return await ReadAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<JobRecord?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var jobs = await LoadAllAsync(ct);
        return jobs.FirstOrDefault(j => j.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task SaveAsync(JobRecord job, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var jobs = await ReadAsync(ct);
            var idx = jobs.FindIndex(j => j.Id.Equals(job.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                jobs[idx] = job;
            else
                jobs.Add(job);

            await WriteAsync(jobs, ct);
            logger.LogDebug("Saved job {Id} ({Name})", job.Id, job.Name);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var jobs = await ReadAsync(ct);
            var removed = jobs.RemoveAll(j => j.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                await WriteAsync(jobs, ct);
                logger.LogInformation("Deleted job {Id}", id);
                return true;
            }
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<List<JobRecord>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(StoreFile))
            return [];

        try
        {
            var json = await File.ReadAllTextAsync(StoreFile, ct);
            return JsonSerializer.Deserialize<List<JobRecord>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read job store at {Path}. Starting fresh.", StoreFile);
            return [];
        }
    }

    private async Task WriteAsync(List<JobRecord> jobs, CancellationToken ct)
    {
        Directory.CreateDirectory(StoreDir);
        var json = JsonSerializer.Serialize(jobs, JsonOptions);
        await File.WriteAllTextAsync(StoreFile, json, ct);
    }
}
