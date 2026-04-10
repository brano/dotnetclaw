using DotnetClaw.Agents;
using DotnetClaw.Jobby;
using DotnetClaw.UI;
using Microsoft.Extensions.Logging;

namespace DotnetClaw.Jobby;

/// <summary>
/// Bridges the Jobby scheduler to <see cref="ClawAgentLoop"/>.
///
/// For <c>isolated = false</c> the job shares the live conversation context.
/// For <c>isolated = true</c> (default) it creates a short-lived shadow renderer
/// so output is captured without polluting the REPL terminal, but uses the same
/// shared kernel/agent instance to avoid the overhead of a full second loop.
/// </summary>
public sealed class ClawJobExecutor(
    ClawAgentLoop agentLoop,
    ILogger<ClawJobExecutor> logger) : IJobExecutor
{
    public async Task<string> ExecuteAsync(string prompt, bool isolated, CancellationToken ct = default)
    {
        logger.LogInformation("JobExecutor: running prompt (isolated={I}): {P}",
            isolated, prompt[..Math.Min(80, prompt.Length)]);

        var sink = new StringCapturingRenderer();

        // Both paths reuse the same ClawAgentLoop (and therefore the same Kernel/plugins).
        // Isolated mode uses a private sink so output doesn't appear in the REPL.
        await agentLoop.RunTurnAsync(prompt, ct, outputSink: isolated ? sink : null);

        return sink.GetText();
    }
}
