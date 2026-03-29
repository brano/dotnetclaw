namespace DotnetClaw.Hub;

/// <summary>
/// Configuration for the DotnetClawHub skills registry client.
/// Bound from <c>DotnetClaw:Hub</c> in appsettings.json.
/// </summary>
public sealed class HubOptions
{
    public const string SectionName = "Hub";

    /// <summary>Base URL of the DotnetClawHub instance.</summary>
    public string Url { get; set; } = "https://localhost:22023";

    /// <summary>When false, hub commands are disabled.</summary>
    public bool Enabled { get; set; } = true;
}
