using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DotnetClaw.Workflowy.Config;
using DotnetClaw.Workflowy.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetClaw.Workflowy.Engine;

/// <summary>
/// Executes a single workflow step (run: / command: type).
/// Pipeline and approval steps are handled by WorkflowEngine and PipelineDispatcher respectively.
/// </summary>
public sealed class StepExecutor(
    IOptions<WorkflowyOptions> options,
    VariableResolver resolver,
    ILogger<StepExecutor> logger)
{
    private readonly WorkflowyOptions _opts = options.Value;

    /// <summary>
    /// Executes a run: step. Resolves variables, launches the shell command, captures output.
    /// </summary>
    public async Task<StepResult> ExecuteAsync(
        WorkflowStep step,
        int stepIndex,
        IReadOnlyDictionary<string, string> context,
        CancellationToken ct)
    {
        var stepName = step.Name ?? $"step_{stepIndex}";
        var startedAt = DateTimeOffset.UtcNow;

        if (step.EffectiveRun is null)
        {
            return new StepResult
            {
                StepIndex = stepIndex, StepName = stepName, StepType = "run",
                Status = StepResultStatus.Failed,
                Stderr = "StepExecutor.ExecuteAsync called without a run: command.",
                StartedAt = startedAt, CompletedAt = DateTimeOffset.UtcNow,
            };
        }

        var resolvedCommand = resolver.Resolve(step.EffectiveRun, context);
        logger.LogInformation("Run step [{Name}]: {Command}", stepName, resolvedCommand);

        return await RunShellCommandAsync(resolvedCommand, stepIndex, stepName, startedAt, ct);
    }

    private async Task<StepResult> RunShellCommandAsync(
        string command,
        int stepIndex,
        string stepName,
        DateTimeOffset startedAt,
        CancellationToken ct)
    {
        var (shell, argPrefix) = GetShell();
        var psi = new ProcessStartInfo
        {
            FileName = shell,
            Arguments = $"{argPrefix}{command}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var limit = _opts.StepOutputCaptureLimitBytes;
        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();
        var stdoutBytes = 0;
        var stderrBytes = 0;
        var wasTruncated = false;

        using var process = new Process { StartInfo = psi };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var bytes = Encoding.UTF8.GetByteCount(e.Data) + 1;
            if (stdoutBytes + bytes <= limit)
            {
                stdoutSb.AppendLine(e.Data);
                stdoutBytes += bytes;
            }
            else wasTruncated = true;
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var bytes = Encoding.UTF8.GetByteCount(e.Data) + 1;
            if (stderrBytes + bytes <= limit)
            {
                stderrSb.AppendLine(e.Data);
                stderrBytes += bytes;
            }
            else wasTruncated = true;
        };

        var timeoutSecs = Math.Clamp(_opts.DefaultStepTimeoutSeconds, 1, _opts.MaxStepTimeoutSeconds);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSecs));

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cts.Token);

            if (wasTruncated)
                stdoutSb.Append("\n[...output truncated - limit exceeded...]");

            return new StepResult
            {
                StepIndex = stepIndex, StepName = stepName, StepType = "run",
                Status = process.ExitCode == 0 ? StepResultStatus.Success : StepResultStatus.Failed,
                Stdout = stdoutSb.ToString().TrimEnd(),
                Stderr = stderrSb.ToString().TrimEnd(),
                ExitCode = process.ExitCode,
                WasTruncated = wasTruncated,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return new StepResult
            {
                StepIndex = stepIndex, StepName = stepName, StepType = "run",
                Status = StepResultStatus.TimedOut,
                ExitCode = -2,
                Stderr = $"[TIMEOUT] Step exceeded {timeoutSecs}s limit.",
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    private (string Shell, string ArgPrefix) GetShell()
    {
        if (_opts.ShellExecutable is not null)
            return (_opts.ShellExecutable, _opts.ShellArgPrefix ?? string.Empty);

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ("cmd.exe", "/c ")
            : ("/bin/sh", "-c ");
    }
}
