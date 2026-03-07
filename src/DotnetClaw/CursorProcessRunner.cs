using System.Diagnostics;
using System.Text;
using DotnetClaw.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetClaw.Plugins;

// ============================================================================
//  Process runner abstraction — keeps CursorPlugin unit-testable
// ============================================================================

/// <summary>
/// Abstracts the actual process spawn so <see cref="CursorPlugin"/> can be
/// tested with a mock runner without needing a real <c>agent.exe</c> on disk.
/// </summary>
public interface ICursorProcessRunner
{
    /// <summary>
    /// Spawns the Cursor agent process with the given arguments and returns
    /// the combined result once the process exits or times out.
    /// </summary>
    Task<CursorProcessOutput> RunAsync(
        CursorInvocation invocation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// All the information needed to spawn one Cursor CLI invocation.
/// Built by <see cref="CursorPlugin"/> and executed by <see cref="ICursorProcessRunner"/>.
/// </summary>
public sealed record CursorInvocation
{
    public required string ExecutablePath { get; init; }
    public required string Arguments { get; init; }
    public required string WorkingDirectory { get; init; }
    public required int TimeoutSeconds { get; init; }
    public required CursorMode Mode { get; init; }
    public required string Prompt { get; init; }
}

/// <summary>Raw output from the OS process — no business logic.</summary>
public sealed record CursorProcessOutput
{
    public required int ExitCode { get; init; }
    public required string Stdout { get; init; }
    public required string Stderr { get; init; }
    public required bool TimedOut { get; init; }
    public required bool ProcessError { get; init; }
    public string? ProcessErrorMessage { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset FinishedAt { get; init; }

    /// <summary>Convenience — true when ExitCode is 0 and no process/timeout error.</summary>
    public bool Success => ExitCode == 0 && !TimedOut && !ProcessError;
}

// ============================================================================
//  Real implementation
// ============================================================================

/// <summary>
/// Production implementation that spawns a real OS process.
/// </summary>
public sealed class CursorProcessRunner(
    IOptions<CursorOptions> options,
    ILogger<CursorProcessRunner> logger) : ICursorProcessRunner
{
    private readonly CursorOptions _options = options.Value;

    public async Task<CursorProcessOutput> RunAsync(
        CursorInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();

        var psi = new ProcessStartInfo
        {
            FileName = invocation.ExecutablePath,
            Arguments = invocation.Arguments,
            WorkingDirectory = invocation.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,    // prevents interactive prompts hanging
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        logger.LogInformation(
            "Spawning Cursor [{Mode}]: {Exe} {Args}",
            invocation.Mode, invocation.ExecutablePath, invocation.Arguments);

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutSb.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrSb.AppendLine(e.Data); };

        var capped = Math.Clamp(invocation.TimeoutSeconds, 5, 1800);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(capped));

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Close(); // signal no stdin input

            await process.WaitForExitAsync(timeoutCts.Token);

            var finishedAt = DateTimeOffset.UtcNow;
            logger.LogInformation(
                "Cursor [{Mode}] exited with code {Code} in {Sec:F1}s",
                invocation.Mode, process.ExitCode, (finishedAt - startedAt).TotalSeconds);

            return new CursorProcessOutput
            {
                ExitCode = process.ExitCode,
                Stdout = stdoutSb.ToString().TrimEnd(),
                Stderr = stderrSb.ToString().TrimEnd(),
                TimedOut = false,
                ProcessError = false,
                StartedAt = startedAt,
                FinishedAt = finishedAt,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout — kill the process tree
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            logger.LogWarning("Cursor [{Mode}] timed out after {Sec}s.", invocation.Mode, capped);

            return new CursorProcessOutput
            {
                ExitCode = -2, TimedOut = true, ProcessError = false,
                Stdout = stdoutSb.ToString().TrimEnd(),
                Stderr = stderrSb.ToString().TrimEnd(),
                StartedAt = startedAt, FinishedAt = DateTimeOffset.UtcNow,
            };
        }
        catch (Exception ex)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            logger.LogError(ex, "Failed to run Cursor [{Mode}].", invocation.Mode);

            return new CursorProcessOutput
            {
                ExitCode = -4, TimedOut = false, ProcessError = true,
                ProcessErrorMessage = ex.Message,
                Stdout = string.Empty, Stderr = string.Empty,
                StartedAt = startedAt, FinishedAt = DateTimeOffset.UtcNow,
            };
        }
    }
}
