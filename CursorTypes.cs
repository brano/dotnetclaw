namespace DotnetClaw.Plugins;

// ============================================================================
//  Cursor CLI — Domain Types
// ============================================================================

/// <summary>
/// Cursor agent invocation modes, mapping directly to the <c>--mode</c> flag.
/// </summary>
public enum CursorMode
{
    /// <summary>
    /// Default. The agent reads the codebase, plans, and applies code edits autonomously.
    /// Equivalent to <c>--mode=agent</c>.
    /// </summary>
    Agent,

    /// <summary>
    /// Planning only. The agent produces a structured step-by-step plan without touching files.
    /// Equivalent to <c>--mode=plan</c>.
    /// </summary>
    Plan,

    /// <summary>
    /// Ask / Q&A. The agent answers questions about the codebase without making changes.
    /// Equivalent to <c>--mode=ask</c>.
    /// </summary>
    Ask,
}

/// <summary>
/// Structured result of a Cursor CLI invocation.
/// </summary>
public sealed class CursorResult
{
    // ── Inputs ────────────────────────────────────────────────────────────────

    public CursorMode Mode { get; init; }
    public string Prompt { get; init; } = string.Empty;
    public string WorkspacePath { get; init; } = string.Empty;
    public string FullCommand { get; init; } = string.Empty;

    // ── Outputs ───────────────────────────────────────────────────────────────

    public int ExitCode { get; init; }
    public bool Success { get; init; }
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }

    // ── Timing ────────────────────────────────────────────────────────────────

    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public TimeSpan Duration => FinishedAt - StartedAt;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a formatted summary suitable for feeding back into the agent's context.
    /// </summary>
    public override string ToString()
    {
        var parts = new List<string>
        {
            $"Mode      : {Mode}",
            $"Workspace : {WorkspacePath}",
            $"Duration  : {Duration.TotalSeconds:F1}s",
            $"ExitCode  : {ExitCode}",
            $"Success   : {Success}",
        };

        if (!string.IsNullOrWhiteSpace(Stdout))
            parts.Add($"\n── Output ──────────────────────────────────────────\n{Stdout.Trim()}");

        if (!string.IsNullOrWhiteSpace(Stderr))
            parts.Add($"\n── Stderr ──────────────────────────────────────────\n{Stderr.Trim()}");

        if (!string.IsNullOrWhiteSpace(ErrorMessage))
            parts.Add($"\n── Error ───────────────────────────────────────────\n{ErrorMessage}");

        return string.Join("\n", parts);
    }

    // ── Factory methods ───────────────────────────────────────────────────────

    public static CursorResult NotFound(string executablePath, CursorMode mode, string prompt) => new()
    {
        Mode = mode, Prompt = prompt, ExitCode = -1, Success = false,
        StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow,
        ErrorMessage =
            $"[NOT FOUND] Cursor agent executable not found at: '{executablePath}'. " +
            "Check CursorOptions.ExecutablePath in appsettings.json or ensure 'agent' is on PATH.",
    };

    public static CursorResult Cancelled(string prompt, CursorMode mode, string workspace) => new()
    {
        Mode = mode, Prompt = prompt, WorkspacePath = workspace,
        ExitCode = -2, Success = false,
        StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow,
        ErrorMessage = "[CANCELLED] Invocation was cancelled by the user or by timeout.",
    };

    public static CursorResult ConfirmationDenied(string prompt, string workspace) => new()
    {
        Mode = CursorMode.Agent, Prompt = prompt, WorkspacePath = workspace,
        ExitCode = -3, Success = false,
        StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow,
        ErrorMessage = "[DENIED] User declined to run in Agent mode. No changes were made.",
    };

    public static CursorResult ProcessError(CursorMode mode, string prompt, string workspace, string message) => new()
    {
        Mode = mode, Prompt = prompt, WorkspacePath = workspace,
        ExitCode = -4, Success = false,
        StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow,
        ErrorMessage = $"[PROCESS ERROR] {message}",
    };
}
