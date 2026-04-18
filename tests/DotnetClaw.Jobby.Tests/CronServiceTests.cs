using System.Reflection;
using DotnetClaw.Jobby;
using DotnetClaw.Jobby.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DotnetClaw.Jobby.Tests;

public class CronServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CronService BuildService(ICronStore store, IJobExecutor executor)
        => new(store, executor, NullLogger<CronService>.Instance);

    /// <summary>
    /// Invokes the private <c>TickAsync</c> method via reflection so tests can
    /// drive the scheduler without starting the background loop.
    /// </summary>
    private static async Task InvokeTickAsync(CronService service)
    {
        var method = typeof(CronService)
            .GetMethod("TickAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(service, new object[] { CancellationToken.None })!;
    }

    // ── ComputeNextRun ────────────────────────────────────────────────────────

    /// <summary>
    /// Calls the internal static <c>CronService.ComputeNextRun</c> via reflection
    /// so the test assembly does not need InternalsVisibleTo.
    /// </summary>
    private static DateTimeOffset? InvokeComputeNextRun(string cronExpression, DateTimeOffset after)
    {
        var method = typeof(CronService)
            .GetMethod("ComputeNextRun", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (DateTimeOffset?)method.Invoke(null, new object[] { cronExpression, after });
    }

    [Fact]
    public void ComputeNextRun_Daily8am_ReturnsNextOccurrence()
    {
        // Arrange: pick a reference time that is before 08:00 UTC today
        var reference = new DateTimeOffset(2025, 4, 10, 6, 0, 0, TimeSpan.Zero);

        // Act
        var next = InvokeComputeNextRun("0 8 * * *", reference);

        // Assert
        Assert.NotNull(next);
        Assert.True(next > reference, "Next run must be after the reference time.");
        Assert.Equal(8, next!.Value.UtcDateTime.Hour);
        Assert.Equal(0, next.Value.UtcDateTime.Minute);
    }

    [Fact]
    public void ComputeNextRun_InvalidExpression_ReturnsNull()
    {
        var result = InvokeComputeNextRun("not-a-cron", DateTimeOffset.UtcNow);
        Assert.Null(result);
    }

    // ── TickAsync — no/skipped jobs ───────────────────────────────────────────

    [Fact]
    public async Task TickAsync_NoDueJobs_ExecutorNotCalled()
    {
        // Arrange
        var storeMock = new Mock<ICronStore>();
        storeMock
            .Setup(s => s.LoadAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var executorMock = new Mock<IJobExecutor>();
        var service = BuildService(storeMock.Object, executorMock.Object);

        // Act
        await InvokeTickAsync(service);

        // Assert
        executorMock.Verify(
            e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TickAsync_DisabledJob_NotExecuted()
    {
        // Arrange
        var disabledJob = new JobRecord
        {
            Name = "Disabled",
            Prompt = "do something",
            Enabled = false,
            NextRunAt = DateTimeOffset.UtcNow.AddHours(-1), // overdue but disabled
        };

        var storeMock = new Mock<ICronStore>();
        storeMock
            .Setup(s => s.LoadAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([disabledJob]);

        var executorMock = new Mock<IJobExecutor>();
        var service = BuildService(storeMock.Object, executorMock.Object);

        // Act
        await InvokeTickAsync(service);

        // Assert
        executorMock.Verify(
            e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TickAsync_JobNotYetDue_NotExecuted()
    {
        // Arrange
        var futureJob = new JobRecord
        {
            Name = "Future",
            Prompt = "do something later",
            Enabled = true,
            NextRunAt = DateTimeOffset.UtcNow.AddHours(1),
        };

        var storeMock = new Mock<ICronStore>();
        storeMock
            .Setup(s => s.LoadAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([futureJob]);

        var executorMock = new Mock<IJobExecutor>();
        var service = BuildService(storeMock.Object, executorMock.Object);

        // Act
        await InvokeTickAsync(service);

        // Assert
        executorMock.Verify(
            e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── TickAsync — successful execution ──────────────────────────────────────

    [Fact]
    public async Task TickAsync_DueRecurringJob_ExecutedAndNextRunAdvanced()
    {
        // Arrange
        var job = new JobRecord
        {
            Id = "testjob1",
            Name = "Daily Report",
            Prompt = "Generate a report",
            Type = JobType.Recurring,
            CronExpression = "0 8 * * *",
            Enabled = true,
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };

        var storeMock = new Mock<ICronStore>();
        storeMock
            .Setup(s => s.LoadAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        storeMock
            .Setup(s => s.SaveAsync(It.IsAny<JobRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var executorMock = new Mock<IJobExecutor>();
        executorMock
            .Setup(e => e.ExecuteAsync(job.Prompt, job.IsolatedSession, It.IsAny<CancellationToken>()))
            .ReturnsAsync("done");

        var service = BuildService(storeMock.Object, executorMock.Object);

        // Act
        await InvokeTickAsync(service);

        // Assert: executor was called
        executorMock.Verify(
            e => e.ExecuteAsync(job.Prompt, job.IsolatedSession, It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: SaveAsync was called with updated fields
        storeMock.Verify(
            s => s.SaveAsync(
                It.Is<JobRecord>(r =>
                    r.Id == "testjob1" &&
                    r.LastRunAt.HasValue &&
                    r.NextRunAt.HasValue &&        // next run must be advanced
                    r.NextRunAt > r.LastRunAt &&
                    r.LastResult == "done"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TickAsync_DueOneShotJob_DisabledAfterRun()
    {
        // Arrange
        var job = new JobRecord
        {
            Id = "oneshotX",
            Name = "One-shot task",
            Prompt = "Send a message",
            Type = JobType.OneShot,
            Enabled = true,
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-2),
        };

        var storeMock = new Mock<ICronStore>();
        storeMock
            .Setup(s => s.LoadAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        storeMock
            .Setup(s => s.SaveAsync(It.IsAny<JobRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var executorMock = new Mock<IJobExecutor>();
        executorMock
            .Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("message sent");

        var service = BuildService(storeMock.Object, executorMock.Object);

        // Act
        await InvokeTickAsync(service);

        // Assert: job is disabled and NextRunAt is cleared
        storeMock.Verify(
            s => s.SaveAsync(
                It.Is<JobRecord>(r =>
                    r.Id == "oneshotX" &&
                    r.Enabled == false &&
                    r.NextRunAt == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── TickAsync — error handling ────────────────────────────────────────────

    [Fact]
    public async Task TickAsync_ExecutorThrows_StoresErrorAndContinues()
    {
        // Arrange
        var job = new JobRecord
        {
            Id = "errjob01",
            Name = "Failing Job",
            Prompt = "do the thing",
            Type = JobType.Recurring,
            CronExpression = "* * * * *",
            Enabled = true,
            NextRunAt = DateTimeOffset.UtcNow.AddSeconds(-10),
        };

        var storeMock = new Mock<ICronStore>();
        storeMock
            .Setup(s => s.LoadAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        storeMock
            .Setup(s => s.SaveAsync(It.IsAny<JobRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var executorMock = new Mock<IJobExecutor>();
        executorMock
            .Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("agent exploded"));

        var service = BuildService(storeMock.Object, executorMock.Object);

        // Act — must not throw
        await InvokeTickAsync(service);

        // Assert: SaveAsync still called with an error result
        storeMock.Verify(
            s => s.SaveAsync(
                It.Is<JobRecord>(r =>
                    r.Id == "errjob01" &&
                    r.LastResult != null &&
                    r.LastResult.StartsWith("[ERROR]")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TickAsync_LongResult_TruncatedTo500()
    {
        // Arrange: executor returns a 600-character string
        var longOutput = new string('A', 600);

        var job = new JobRecord
        {
            Id = "truncjob1",
            Name = "Verbose Job",
            Prompt = "verbose task",
            Type = JobType.Recurring,
            CronExpression = "* * * * *",
            Enabled = true,
            NextRunAt = DateTimeOffset.UtcNow.AddSeconds(-5),
        };

        var storeMock = new Mock<ICronStore>();
        storeMock
            .Setup(s => s.LoadAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        storeMock
            .Setup(s => s.SaveAsync(It.IsAny<JobRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var executorMock = new Mock<IJobExecutor>();
        executorMock
            .Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(longOutput);

        var service = BuildService(storeMock.Object, executorMock.Object);

        // Act
        await InvokeTickAsync(service);

        // Assert: LastResult is 500 chars + the ellipsis character
        storeMock.Verify(
            s => s.SaveAsync(
                It.Is<JobRecord>(r =>
                    r.Id == "truncjob1" &&
                    r.LastResult != null &&
                    r.LastResult.Length == 501 && // 500 chars + "…"
                    r.LastResult.EndsWith("…")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
