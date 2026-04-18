using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetClaw.Jobby.Models;
using Microsoft.Extensions.Logging;

namespace DotnetClaw.Jobby;

/// <summary>
/// Reads and writes the job list from/to a JSON file under
/// <c>~/.dotnetclaw/cron/jobs.json</c> (or a custom directory for tests).
///
/// Thread-safety: all public methods are async and serialise access with a
/// <see cref="SemaphoreSlim"/> so the CronService and plugin can both write safely.
/// </summary>
public sealed class CronStore : ICronStore
{
    private static readonly string DefaultStoreDir =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnetclaw", "cron");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _storeDir;
    private readonly string _storeFile;
    private readonly ILogger<CronStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Production constructor — uses <c>~/.dotnetclaw/cron/</c>.</summary>
    public CronStore(ILogger<CronStore> logger) : this(logger, null) { }

    /// <summary>
    /// Test constructor — uses <paramref name="storeBaseDir"/> so tests can
    /// write to a temp directory instead of the real user profile.
    /// </summary>
    internal CronStore(ILogger<CronStore> logger, string? storeBaseDir)
    {
        _logger    = logger;
        _storeDir  = storeBaseDir ?? DefaultStoreDir;
        _storeFile = Path.Combine(_storeDir, "jobs.json");
    }

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
            _logger.LogDebug("Saved job {Id} ({Name})", job.Id, job.Name);
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
                _logger.LogInformation("Deleted job {Id}", id);
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
        if (!File.Exists(_storeFile))
            return [];

        try
        {
            var json = await File.ReadAllTextAsync(_storeFile, ct);
            return JsonSerializer.Deserialize<List<JobRecord>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read job store at {Path}. Starting fresh.", _storeFile);
            return [];
        }
    }

    private async Task WriteAsync(List<JobRecord> jobs, CancellationToken ct)
    {
        Directory.CreateDirectory(_storeDir);
        var json = JsonSerializer.Serialize(jobs, JsonOptions);
        await File.WriteAllTextAsync(_storeFile, json, ct);
    }
}
