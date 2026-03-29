namespace DotnetClawHub.Models;

public class Skill
{
    public int Id { get; set; }

    /// <summary>URL-safe slug, unique identifier (e.g. "dotnet-expert")</summary>
    public string Name { get; set; } = "";

    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "1.0.0";

    /// <summary>Comma-separated tags (e.g. "dotnet,csharp,expert")</summary>
    public string Tags { get; set; } = "";

    /// <summary>Full SKILL.md content</summary>
    public string SkillMarkdown { get; set; } = "";

    public int Downloads { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string[] TagList => Tags
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
