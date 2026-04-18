using DotnetClaw.Jobby.Models;

namespace DotnetClaw.Jobby;

/// <summary>
/// Abstraction over <see cref="CronStore"/> to enable unit testing without
/// touching the real filesystem (Moq cannot mock sealed classes).
/// </summary>
public interface ICronStore
{
    Task<List<JobRecord>> LoadAllAsync(CancellationToken ct = default);
    Task<JobRecord?> GetByIdAsync(string id, CancellationToken ct = default);
    Task SaveAsync(JobRecord job, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}
