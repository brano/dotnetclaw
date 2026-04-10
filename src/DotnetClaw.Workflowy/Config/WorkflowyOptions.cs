namespace DotnetClaw.Workflowy.Config;

/// <summary>
/// Configuration for DotnetClaw.Workflowy.
/// Bound from the <c>Workflowy</c> section (standalone) or <c>DotnetClaw:Workflowy</c>
/// section (when embedded in DotnetClaw).
/// </summary>
public sealed class WorkflowyOptions
{
    public const string SectionName = "Workflowy";

    /// <summary>Path to the SQLite database file. ~ is expanded to the user's home directory.</summary>
    public string DatabasePath { get; set; } = "~/.workflowy/workflowy.db";

    /// <summary>Maximum bytes captured per step for stdout and stderr individually. Default 32 KB.</summary>
    public int StepOutputCaptureLimitBytes { get; set; } = 32_768;

    /// <summary>Default step timeout in seconds.</summary>
    public int DefaultStepTimeoutSeconds { get; set; } = 60;

    /// <summary>Maximum allowed step timeout in seconds.</summary>
    public int MaxStepTimeoutSeconds { get; set; } = 600;

    /// <summary>Override the shell executable for run: steps (e.g. "bash"). Defaults to platform shell.</summary>
    public string? ShellExecutable { get; set; }

    /// <summary>Argument prefix for the shell (e.g. "-c "). Defaults to platform default.</summary>
    public string? ShellArgPrefix { get; set; }

    /// <summary>Resolved absolute path to the database file (expands ~).</summary>
    public string ResolvedDatabasePath
    {
        get
        {
            var path = DatabasePath;
            if (path.StartsWith("~/", StringComparison.Ordinal)
                || path.StartsWith("~\\", StringComparison.Ordinal))
            {
                path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    path[2..]);
            }
            return Path.GetFullPath(path);
        }
    }
}
