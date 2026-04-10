using DotnetClaw.Workflowy.Models;
using Microsoft.Extensions.Logging;

namespace DotnetClaw.Workflowy.Engine;

/// <summary>
/// Handles pipeline: directives in workflow steps.
/// Each directive is in the form "verb.noun [--flag value ...]".
///
/// Supported directives:
///   llm.invoke --prompt "..."   — invokes an LLM (stub in standalone mode)
/// </summary>
public sealed class PipelineDispatcher(ILogger<PipelineDispatcher> logger)
{
    public Task<StepResult> DispatchAsync(
        string directive,
        int stepIndex,
        string stepName,
        IReadOnlyDictionary<string, string> context,
        CancellationToken ct)
    {
        logger.LogInformation("Pipeline [{Name}]: {Directive}", stepName, directive);
        var startedAt = DateTimeOffset.UtcNow;

        var parts = directive.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;

        StepResult result = verb switch
        {
            "llm.invoke" => new StepResult
            {
                StepIndex = stepIndex, StepName = stepName, StepType = "pipeline",
                Status = StepResultStatus.Success,
                Stdout = "[llm.invoke: LLM provider not wired in standalone mode. " +
                         "When running inside DotnetClaw the agent handles LLM calls directly.]",
                ExitCode = 0,
                StartedAt = startedAt, CompletedAt = DateTimeOffset.UtcNow,
            },
            _ => new StepResult
            {
                StepIndex = stepIndex, StepName = stepName, StepType = "pipeline",
                Status = StepResultStatus.Failed,
                Stderr = $"Unknown pipeline directive: '{verb}'. Supported: llm.invoke",
                ExitCode = -1,
                StartedAt = startedAt, CompletedAt = DateTimeOffset.UtcNow,
            }
        };

        return Task.FromResult(result);
    }
}
