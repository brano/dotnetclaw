namespace DotnetClaw.Config;

/// <summary>
/// Configuration for the Cursor CLI (agent.exe) plugin.
/// Bound from <c>appsettings.json</c> under the <c>DotnetClaw:Cursor</c> key.
/// </summary>
public sealed class CursorOptions
{
    /// <summary>
    /// Path to the Cursor agent executable.
    ///
    /// Common locations:
    ///   Windows : %LOCALAPPDATA%\Programs\cursor\resources\app\bin\agent.exe
    ///   macOS   : /Applications/Cursor.app/Contents/Resources/app/bin/agent
    ///   Linux   : ~/.local/share/cursor/resources/app/bin/agent
    ///
    /// If set to just <c>"agent"</c> or <c>"cursor"</c>, the executable must be on PATH.
    /// </summary>
    public string ExecutablePath { get; set; } = "agent";

    /// <summary>
    /// Default timeout in seconds for a Cursor agent invocation.
    /// Agent mode (which may edit files) may need longer than Ask/Plan.
    /// Max is capped at 1800 (30 min) regardless of this setting.
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// When <c>true</c>, the plugin will print a confirmation prompt to the
    /// console and wait for the user to press Y before running in Agent mode.
    /// Has no effect in Plan or Ask mode (both are read-only).
    /// </summary>
    public bool RequireConfirmationForAgentMode { get; set; } = true;

    /// <summary>
    /// Optional Cursor model override, e.g. <c>"claude-3-5-sonnet"</c> or <c>"gpt-4o"</c>.
    /// Leave empty to use the model configured inside Cursor itself.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Extra CLI flags appended verbatim to every invocation.
    /// Example: <c>"--no-telemetry --quiet"</c>
    /// </summary>
    public string ExtraFlags { get; set; } = string.Empty;

    /// <summary>
    /// When <c>true</c>, the plugin passes <c>--yes</c> (or equivalent auto-approve flag)
    /// to suppress interactive prompts inside the Cursor agent.
    /// Only applies to Agent mode. Ignored in Plan / Ask.
    /// </summary>
    public bool AutoApproveInAgentMode { get; set; } = false;
}
