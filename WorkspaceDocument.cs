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
/// Describes how the workspace was loaded during a session initialisation.
/// </summary>
public sealed record WorkspaceLoadResult
{
    public required string WorkspacePath { get; init; }
    public required IReadOnlyList<WorkspaceDocument> Documents { get; init; }
    public required IReadOnlyList<string> SkippedFiles { get; init; }
    public required DateTimeOffset LoadedAt { get; init; }

    public bool IsEmpty => Documents.Count == 0;

    public string Summary =>
        IsEmpty
            ? $"Workspace at '{WorkspacePath}' is empty — no identity documents found."
            : $"Loaded {Documents.Count} workspace document(s): {string.Join(", ", Documents.Select(d => d.Name))}";
}
