namespace DotnetClaw.Workspace;

/// <summary>
/// Represents a single identity / context document loaded from the workspace folder.
/// </summary>
public sealed record WorkspaceDocument
{
    /// <summary>File name without extension, e.g. "SOUL", "USER", "AGENTS".</summary>
    public required string Name { get; init; }

    /// <summary>Full path to the file on disk.</summary>
    public required string FilePath { get; init; }

    /// <summary>Raw markdown content of the document.</summary>
    public required string Content { get; init; }

    /// <summary>UTC timestamp of when the file was last read.</summary>
    public required DateTimeOffset LoadedAt { get; init; }

    /// <summary>UTC last-write time of the file at load time.</summary>
    public required DateTimeOffset FileModifiedAt { get; init; }

    /// <summary>
    /// Render this document as a system-context block suitable for injection
    /// into the agent's system prompt.
    /// </summary>
    public string ToContextBlock() =>
        $"""
         ===== {Name} =====
         {Content.Trim()}
         ==================
         """;
}

/// <summary>
/// Represents a single skill loaded from a <c>workspace/skills/{name}/SKILL.md</c> file.
/// Skills are how-to guides the LLM can reference to complete specialised tasks.
/// </summary>
public sealed record SkillDocument
{
    /// <summary>
    /// The skill folder name, e.g. "weather", "github", "copilot-cli".
    /// Derived from the parent directory of the SKILL.md file.
    /// </summary>
    public required string SkillName { get; init; }

    /// <summary>Full path to the SKILL.md file on disk.</summary>
    public required string FilePath { get; init; }

    /// <summary>Raw markdown content of SKILL.md.</summary>
    public required string Content { get; init; }

    /// <summary>UTC timestamp of when the file was last read.</summary>
    public required DateTimeOffset LoadedAt { get; init; }

    /// <summary>UTC last-write time of the file at load time.</summary>
    public required DateTimeOffset FileModifiedAt { get; init; }

    /// <summary>
    /// Render this skill as a context block for injection into the system prompt.
    /// </summary>
    public string ToContextBlock() =>
        $"""
         ===== SKILL: {SkillName} =====
         {Content.Trim()}
         ==============================
         """;
}

/// <summary>
/// Describes how the workspace was loaded during a session initialisation.
/// </summary>
public sealed record WorkspaceLoadResult
{
    public required string WorkspacePath { get; init; }
    public required IReadOnlyList<WorkspaceDocument> Documents { get; init; }
    public required IReadOnlyList<SkillDocument> Skills { get; init; }
    public required IReadOnlyList<string> SkippedFiles { get; init; }
    public required DateTimeOffset LoadedAt { get; init; }

    public bool IsEmpty => Documents.Count == 0 && Skills.Count == 0;

    public string Summary =>
        IsEmpty
            ? $"Workspace at '{WorkspacePath}' is empty — no identity documents or skills found."
            : $"Loaded {Documents.Count} workspace document(s): {string.Join(", ", Documents.Select(d => d.Name))}"
              + (Skills.Count > 0
                  ? $" | {Skills.Count} skill(s): {string.Join(", ", Skills.Select(s => s.SkillName))}"
                  : string.Empty);
}
