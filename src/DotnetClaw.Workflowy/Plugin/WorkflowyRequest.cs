using System.Text.Json.Serialization;

namespace DotnetClaw.Workflowy.Plugin;

/// <summary>Inbound tool call JSON sent by the DotnetClaw agent.</summary>
public sealed class WorkflowyRequest
{
    /// <summary>"run" or "resume".</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    /// <summary>For action=run: "/path/to/workflow.yaml --arg1 value1".</summary>
    [JsonPropertyName("pipeline")]
    public string? Pipeline { get; set; }

    /// <summary>For action=run: timeout in milliseconds. Default 30000.</summary>
    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; set; } = 30_000;

    /// <summary>For action=resume: the resume token from a prior needs_approval response.</summary>
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    /// <summary>For action=resume: true to approve, false to reject.</summary>
    [JsonPropertyName("approve")]
    public bool Approve { get; set; }
}
