namespace DotnetClaw.Hub;

/// <summary>Skill metadata returned by the DotnetClawHub search API.</summary>
public sealed record HubSkillInfo(
    int Id,
    string Name,
    string DisplayName,
    string Description,
    string Author,
    string Version,
    string Tags,
    int Downloads,
    DateTime CreatedAt,
    DateTime UpdatedAt);
