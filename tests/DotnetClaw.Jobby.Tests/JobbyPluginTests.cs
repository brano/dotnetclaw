using DotnetClaw.Jobby;
using DotnetClaw.Jobby.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DotnetClaw.Jobby.Tests;

public class JobbyPluginTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static JobbyPlugin BuildPlugin(ICronStore store, IJobExecutor executor)
        => new(store, executor, NullLogger<JobbyPlugin>.Instance);

    private static Mock<ICronStore> EmptyStoreMock()
    {
        var mock = new Mock<ICronStore>();
        mock.Setup(s => s.LoadAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        mock.Setup(s => s.SaveAsync(It.IsAny<JobRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        mock.Setup(s => s.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobRecord?)null);
        return mock;
    }

    // ── ScheduleJobAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task ScheduleJobAsync_InvalidCron_ReturnsErrorMessage()
    {
        var storeMock = EmptyStoreMock();
        var executorMock = new Mock<IJobExecutor>();
        var plugin = BuildPlugin(storeMock.Object, executorMock.Object);

        var result = await plugin.ScheduleJobAsync("My Job", "do stuff", "not-a-cron");

        Assert.Contains("Invalid CRON expression", result);
        storeMock.Verify(s => s.SaveAsync(It.IsAny<JobRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScheduleJobAsync_ValidCron_SavesJobAndReturnsId()
    {
        var storeMock = EmptyStoreMock();
        var executorMock = new Mock<IJobExecutor>();
        var plugin = BuildPlugin(storeMock.Object, executorMock.Object);

        var result = await plugin.ScheduleJobAsync("Daily Standup", "Write standup notes", "0 9 * * 1-5");

        // Should save a Recurring job exactly once
        storeMock.Verify(
            s => s.SaveAsync(
                It.Is<JobRecord>(r =>
                    r.Type == JobType.Recurring &&
                    r.Name == "Daily Standup" &&
                    r.CronExpression == "0 9 * * 1-5"),
                It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.Contains("Job scheduled. ID:", result);
    }

    // ── ScheduleOnceAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task ScheduleOnceAsync_PastRunAt_ReturnsError()
    {
        var storeMock = EmptyStoreMock();
        var executorMock = new Mock<IJobExecutor>();
        var plugin = BuildPlugin(storeMock.Object, executorMock.Object);

        var pastTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        var result = await plugin.ScheduleOnceAsync("Past Task", "do it", pastTime);

        Assert.Contains("runAt must be in the future", result);
        storeMock.Verify(s => s.SaveAsync(It.IsAny<JobRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScheduleOnceAsync_FutureRunAt_SavesAndReturnsId()
    {
        var storeMock = EmptyStoreMock();
        var executorMock = new Mock<IJobExecutor>();
        var plugin = BuildPlugin(storeMock.Object, executorMock.Object);

        var futureTime = DateTimeOffset.UtcNow.AddHours(2);
        var result = await plugin.ScheduleOnceAsync("Future Task", "do it later", futureTime);

        storeMock.Verify(
            s => s.SaveAsync(
                It.Is<JobRecord>(r =>
                    r.Type == JobType.OneShot &&
                    r.RunAt == futureTime &&
                    r.NextRunAt == futureTime),
                It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.Contains("One-shot job scheduled.", result);
    }

    // ── ListJobsAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ListJobsAsync_NoJobs_ReturnsNoJobsMessage()
    {
        var storeMock = EmptyStoreMock();
        var plugin = BuildPlugin(storeMock.Object, new Mock<IJobExecutor>().Object);

        var result = await plugin.ListJobsAsync();

        Assert.Equal("No jobs scheduled.", result);
    }

    [Fact]
    public async Task ListJobsAsync_WithJobs_ReturnsTableWithAllJobs()
    {
        var job1 = new JobRecord { Name = "Alpha Job", Prompt = "alpha", Type = JobType.Recurring, CronExpression = "0 8 * * *" };
        var job2 = new JobRecord { Name = "Beta Job", Prompt = "beta", Type = JobType.OneShot, RunAt = DateTimeOffset.UtcNow.AddDays(1) };

        var storeMock = new Mock<ICronStore>();
        storeMock
            .Setup(s => s.LoadAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([job1, job2]);

        var plugin = BuildPlugin(storeMock.Object, new Mock<IJobExecutor>().Object);

        var result = await plugin.ListJobsAsync();

        Assert.Contains("Alpha Job", result);
        Assert.Contains("Beta Job", result);
    }

    // ── GetJobAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetJobAsync_NotFound_ReturnsNotFound()
    {
        var storeMock = EmptyStoreMock();
        var plugin = BuildPlugin(storeMock.Object, new Mock<IJobExecutor>().Object);

        var result = await plugin.GetJobAsync("unknown1");

        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task GetJobAsync_Found_ReturnsDetailString()
    {
        var job = new JobRecord
        {
            Id = "detailjob",
            Name = "Detail Job",
            Prompt = "inspect everything",
            Type = JobType.Recurring,
            CronExpression = "0 6 * * *",
        };

        var storeMock = new Mock<ICronStore>();
        storeMock
            .Setup(s => s.GetByIdAsync("detailjob", It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        var plugin = BuildPlugin(storeMock.Object, new Mock<IJobExecutor>().Object);

        var result = await plugin.GetJobAsync("detailjob");

        Assert.Contains(job.Id, result);
        Assert.Contains(job.Name, result);
        Assert.Contains(job.Prompt, result);
    }

    // ── DeleteJobAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteJobAsync_Exists_ReturnsDeletedMessage()
    {
        var storeMock = new Mock<ICronStore>();
        storeMock
            .Setup(s => s.DeleteAsync("del00001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var plugin = BuildPlugin(storeMock.Object, new Mock<IJobExecutor>().Object);

        var result = await plugin.DeleteJobAsync("del00001");

        Assert.Contains("deleted", result);
    }

    [Fact]
    public async Task DeleteJobAsync_NotFound_ReturnsNotFound()
    {
        var storeMock = new Mock<ICronStore>();
        storeMock
            .Setup(s => s.DeleteAsync("missing1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var plugin = BuildPlugin(storeMock.Object, new Mock<IJobExecutor>().Object);

        var result = await plugin.DeleteJobAsync("missing1");

        Assert.Contains("not found", result);
    }

    // ── DisableJobAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task DisableJobAsync_Exists_DisablesJob()
    {
        var job = new JobRecord
        {
            Id = "disabjob",
            Name = "Active Job",
            Prompt = "something",
            Enabled = true,
            Type = JobType.Recurring,
            CronExpression = "0 12 * * *",
            NextRunAt = DateTimeOffset.UtcNow.AddHours(1),
        };

        var storeMock = new Mock<ICronStore>();
        storeMock
            .Setup(s => s.GetByIdAsync("disabjob", It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        storeMock
            .Setup(s => s.SaveAsync(It.IsAny<JobRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var plugin = BuildPlugin(storeMock.Object, new Mock<IJobExecutor>().Object);

        await plugin.DisableJobAsync("disabjob");

        storeMock.Verify(
            s => s.SaveAsync(
                It.Is<JobRecord>(r => r.Id == "disabjob" && r.Enabled == false),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── EnableJobAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task EnableJobAsync_RecurringJob_RecomputesNextRunAt()
    {
        var job = new JobRecord
        {
            Id = "enabjob1",
            Name = "Re-enable Me",
            Prompt = "do stuff",
            Enabled = false,
            Type = JobType.Recurring,
            CronExpression = "0 8 * * *",
            NextRunAt = null,
        };

        var storeMock = new Mock<ICronStore>();
        storeMock
            .Setup(s => s.GetByIdAsync("enabjob1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        storeMock
            .Setup(s => s.SaveAsync(It.IsAny<JobRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var plugin = BuildPlugin(storeMock.Object, new Mock<IJobExecutor>().Object);

        await plugin.EnableJobAsync("enabjob1");

        storeMock.Verify(
            s => s.SaveAsync(
                It.Is<JobRecord>(r =>
                    r.Id == "enabjob1" &&
                    r.Enabled == true &&
                    r.NextRunAt.HasValue),        // recomputed from UtcNow
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── RunJobNowAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task RunJobNowAsync_NotFound_ReturnsNotFound()
    {
        var storeMock = EmptyStoreMock();
        var plugin = BuildPlugin(storeMock.Object, new Mock<IJobExecutor>().Object);

        var result = await plugin.RunJobNowAsync("nojob001");

        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task RunJobNowAsync_Exists_ExecutesAndReturnsResult()
    {
        var job = new JobRecord
        {
            Id = "runjob01",
            Name = "Run Now",
            Prompt = "summarise the logs",
            IsolatedSession = true,
        };

        var storeMock = new Mock<ICronStore>();
        storeMock
            .Setup(s => s.GetByIdAsync("runjob01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        storeMock
            .Setup(s => s.SaveAsync(It.IsAny<JobRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var executorMock = new Mock<IJobExecutor>();
        executorMock
            .Setup(e => e.ExecuteAsync("summarise the logs", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync("output text");

        var plugin = BuildPlugin(storeMock.Object, executorMock.Object);

        var result = await plugin.RunJobNowAsync("runjob01");

        Assert.StartsWith("Job executed.", result);
        Assert.Contains("output text", result);
    }

    [Fact]
    public async Task RunJobNowAsync_ExecutorThrows_ReturnsErrorInResult()
    {
        var job = new JobRecord
        {
            Id = "errrun01",
            Name = "Explosive Job",
            Prompt = "blow up",
            IsolatedSession = false,
        };

        var storeMock = new Mock<ICronStore>();
        storeMock
            .Setup(s => s.GetByIdAsync("errrun01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        storeMock
            .Setup(s => s.SaveAsync(It.IsAny<JobRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var executorMock = new Mock<IJobExecutor>();
        executorMock
            .Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("kaboom"));

        var plugin = BuildPlugin(storeMock.Object, executorMock.Object);

        var result = await plugin.RunJobNowAsync("errrun01");

        // Result should report the execution attempt completed (not rethrow)
        Assert.StartsWith("Job executed.", result);
        Assert.Contains("[ERROR]", result);
        Assert.Contains("kaboom", result);
    }
}
