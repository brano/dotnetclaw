namespace DotnetClaw.Web.Services;

// ============================================================================
//  AppState — singleton that holds UI state shared across Blazor components
// ============================================================================

public enum OnboardingStep { Welcome, ModelSetup, TelegramSetup, Done }

public sealed class AppState
{
    // ── Onboarding ────────────────────────────────────────────────────────────
    public bool OnboardingComplete { get; set; }
    public OnboardingStep CurrentOnboardingStep { get; set; }

    // ── Agent status ──────────────────────────────────────────────────────────
    public bool AgentRunning { get; private set; }
    public string AgentStatus { get; private set; } = "Idle";
    public int TotalTurns { get; private set; }
    public int TotalTokensUsed { get; private set; }
    public DateTime? LastActivityAt { get; private set; }

    // ── Telegram ──────────────────────────────────────────────────────────────
    public bool TelegramConnected { get; set; }
    public int TelegramMessagesReceived { get; set; }

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action? OnChange;

    public void SetAgentRunning(bool running, string status = "")
    {
        AgentRunning = running;
        AgentStatus = running ? (string.IsNullOrWhiteSpace(status) ? "Processing…" : status) : "Idle";
        if (running) LastActivityAt = DateTime.UtcNow;
        NotifyStateChanged();
    }

    public void RecordTurn(int tokensUsed = 0)
    {
        TotalTurns++;
        TotalTokensUsed += tokensUsed;
        LastActivityAt = DateTime.UtcNow;
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => OnChange?.Invoke();
}
