namespace DotnetClaw.Jobby;

/// <summary>
/// Abstraction over the agent loop so Jobby stays decoupled from the
/// concrete <c>ClawAgentLoop</c> implementation.
/// Implemented by <c>ClawJobExecutor</c> in the host project.
/// </summary>
public interface IJobExecutor
{
    /// <summary>
    /// Execute <paramref name="prompt"/> through the agent and return its text response.
    /// </summary>
    /// <param name="prompt">The user message / task to run.</param>
    /// <param name="isolated">
    /// When true a temporary session is created so the job does not pollute
    /// the main conversation history.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> ExecuteAsync(string prompt, bool isolated, CancellationToken ct = default);
}
