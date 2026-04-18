using System.Reflection;
using DotnetClaw.Jobby;
using DotnetClaw.Jobby.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetClaw.Jobby.Tests;

/// <summary>
/// Integration tests for <see cref="CronStore"/>.
///
/// Uses the internal test constructor via reflection so no InternalsVisibleTo
/// attribute is needed in the production project.
/// </summary>
public class CronStoreTests : IDisposable
{
    private readonly string _tempDir;

    public CronStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CronStoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Instantiates <see cref="CronStore"/> using the internal test constructor
    /// via reflection, pointing at the per-test temp directory.
    /// </summary>
    private CronStore CreateStore()
    {
        var ctor = typeof(CronStore).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(ILogger<CronStore>), typeof(string) },
            modifiers: null)!;

        return (CronStore)ctor.Invoke(new object[] { NullLogger<CronStore>.Instance, _tempDir });
    }

    private static JobRecord MakeJob(string name = "Test Job", string prompt = "do the thing")
        => new() { Name = name, Prompt = prompt };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAllAsync_EmptyStore_ReturnsEmptyList()
    {
        var store = CreateStore();

        var jobs = await store.LoadAllAsync();

        Assert.Empty(jobs);
    }

    [Fact]
    public async Task SaveAsync_NewJob_PersistedToDisk()
    {
        var store = CreateStore();
        var job = MakeJob("Persist Me");

        await store.SaveAsync(job);

        // Create a fresh store instance pointing at the same directory to
        // confirm the data was written to disk, not just held in memory.
        var freshStore = CreateStore();
        var loaded = await freshStore.LoadAllAsync();

        Assert.Single(loaded);
        Assert.Equal(job.Id, loaded[0].Id);
        Assert.Equal("Persist Me", loaded[0].Name);
    }

    [Fact]
    public async Task SaveAsync_ExistingJob_UpdatedInPlace()
    {
        var store = CreateStore();
        var job = MakeJob("Original Name");
        await store.SaveAsync(job);

        // Mutate and save again
        job.Name = "Updated Name";
        job.Prompt = "updated prompt";
        await store.SaveAsync(job);

        var loaded = await store.LoadAllAsync();

        // Should still be exactly one record
        Assert.Single(loaded);
        Assert.Equal("Updated Name", loaded[0].Name);
        Assert.Equal("updated prompt", loaded[0].Prompt);
    }

    [Fact]
    public async Task GetByIdAsync_ExistingId_ReturnsJob()
    {
        var store = CreateStore();
        var job = MakeJob("Find Me");
        await store.SaveAsync(job);

        var found = await store.GetByIdAsync(job.Id);

        Assert.NotNull(found);
        Assert.Equal(job.Id, found!.Id);
        Assert.Equal("Find Me", found.Name);
    }

    [Fact]
    public async Task GetByIdAsync_UnknownId_ReturnsNull()
    {
        var store = CreateStore();

        var found = await store.GetByIdAsync("nonexistent");

        Assert.Null(found);
    }

    [Fact]
    public async Task DeleteAsync_ExistingJob_ReturnsTrueAndRemoves()
    {
        var store = CreateStore();
        var job = MakeJob("Delete Me");
        await store.SaveAsync(job);

        var deleted = await store.DeleteAsync(job.Id);

        Assert.True(deleted);
        var remaining = await store.LoadAllAsync();
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_ReturnsFalse()
    {
        var store = CreateStore();

        var deleted = await store.DeleteAsync("does-not-exist");

        Assert.False(deleted);
    }

    [Fact]
    public async Task LoadAllAsync_MultipleJobs_ReturnsAll()
    {
        var store = CreateStore();
        var job1 = MakeJob("Job One");
        var job2 = MakeJob("Job Two");
        var job3 = MakeJob("Job Three");

        await store.SaveAsync(job1);
        await store.SaveAsync(job2);
        await store.SaveAsync(job3);

        var loaded = await store.LoadAllAsync();

        Assert.Equal(3, loaded.Count);
        Assert.Contains(loaded, j => j.Name == "Job One");
        Assert.Contains(loaded, j => j.Name == "Job Two");
        Assert.Contains(loaded, j => j.Name == "Job Three");
    }

    [Fact]
    public async Task ConcurrentSaves_ThreadSafe()
    {
        var store = CreateStore();

        // Build 10 distinct jobs upfront so IDs are known
        var jobs = Enumerable.Range(1, 10)
            .Select(i => new JobRecord { Name = $"Concurrent Job {i}", Prompt = $"task {i}" })
            .ToList();

        // Save all concurrently
        await Task.WhenAll(jobs.Select(j => store.SaveAsync(j)));

        var loaded = await store.LoadAllAsync();

        Assert.Equal(10, loaded.Count);

        // All 10 distinct IDs must be present
        var loadedIds = loaded.Select(j => j.Id).ToHashSet();
        foreach (var job in jobs)
            Assert.Contains(job.Id, loadedIds);
    }
}
