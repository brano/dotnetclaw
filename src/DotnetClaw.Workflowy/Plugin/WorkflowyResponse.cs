using System.Text.Json.Serialization;

namespace DotnetClaw.Workflowy.Plugin;

/// <summary>
/// Outbound JSON envelope returned by Workflowy tool calls.
/// Status values: "ok" | "needs_approval" | "cancelled" | "error"
/// </summary>
public sealed class WorkflowyResponse(bool ok, string status)
{
    [JsonPropertyName("ok")]
    public bool Ok { get; } = ok;

    [JsonPropertyName("status")]
    public string Status { get; } = status;

    [JsonPropertyName("output")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<OutputItem>? Output { get; set; }

    [JsonPropertyName("requiresApproval")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ApprovalRequest? RequiresApproval { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    public static WorkflowyResponse Failure(string message) =>
        new(false, "error") { Error = message };
}

public sealed record OutputItem(
    [property: JsonPropertyName("summary")] string Summary);

public sealed record ApprovalRequest(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("items")] IReadOnlyList<string> Items,
    [property: JsonPropertyName("resumeToken")] string ResumeToken);

/// <summary>DTO shared between WorkflowyPlugin and WorkflowyApprovalService to describe a pending approval.</summary>
public sealed record PendingApprovalDto(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("workflowName")] string WorkflowName,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("items")] IReadOnlyList<string> Items,
    [property: JsonPropertyName("requestedAt")] DateTimeOffset RequestedAt);
