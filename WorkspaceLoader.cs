using DotnetClaw.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetClaw.Workspace;

/// <summary>
/// Scans the <c>./workspace</c> folder and loads identity / context documents
/// (SOUL.md, USER.md, AGENTS.md, and any other *.md files) into memory.
///
/// Loading rules:
///   1. Known priority documents are loaded first, in the order defined by
///      <see cref="DotnetClawOptions.WorkspaceDocumentPriority"/>.
///   2. Any remaining *.md files in the folder are appended alphabetically.
///   3. Non-.md files are listed in <see cref="WorkspaceLoadResult.SkippedFiles"/>.
///   4. Missing priority files are silently skipped (workspace is optional).
/// </summary>
public sealed class WorkspaceLoader(
    IOptions<DotnetClawOptions> options,
    ILogger<WorkspaceLoader> logger)
{
    private readonly DotnetClawOptions _options = options.Value;

    // Cache so Reset() can reload fresh
    private WorkspaceLoadResult? _cached;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Load (or return cached) workspace documents.
    /// Call <see cref="ReloadAsync"/> to force a fresh read from disk.
    /// </summary>
    public async Task<WorkspaceLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null)
                return _cached;

            _cached = await LoadFromDiskAsync(cancellationToken);
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Force a fresh read from disk and update the cache.
    /// </summary>
    public async Task<WorkspaceLoadResult> ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            _cached = await LoadFromDiskAsync(cancellationToken);
            logger.LogInformation("Workspace reloaded: {Summary}", _cached.Summary);
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Retrieve a single document by name (case-insensitive), or null if not loaded.
    /// </summary>
    public async Task<WorkspaceDocument?> GetDocumentAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var result = await LoadAsync(cancellationToken);
        return result.Documents.FirstOrDefault(d =>
            d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Build the full context block to be injected into the system prompt.
    /// Returns an empty string when the workspace folder is absent or empty.
    /// </summary>
    public async Task<string> BuildContextBlockAsync(CancellationToken cancellationToken = default)
    {
        var result = await LoadAsync(cancellationToken);
        if (result.IsEmpty) return string.Empty;

        var sections = result.Documents.Select(d => d.ToContextBlock());
        return $"""
                ── WORKSPACE IDENTITY DOCUMENTS ──────────────────────────────────────────────
                The following identity documents were loaded from the workspace folder.
                Treat them as ground truth about who you are, the user, and how agents behave.

                {string.Join("\n\n", sections)}

                ── END OF WORKSPACE DOCUMENTS ────────────────────────────────────────────────
                """;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task<WorkspaceLoadResult> LoadFromDiskAsync(CancellationToken cancellationToken)
    {
        var workspacePath = ResolveWorkspacePath();
        var skipped = new List<string>();
        var docs = new List<WorkspaceDocument>();

        if (!Directory.Exists(workspacePath))
        {
            logger.LogInformation(
                "Workspace folder not found at '{Path}'. Skipping identity document load.",
                workspacePath);

            return new WorkspaceLoadResult
            {
                WorkspacePath = workspacePath,
                Documents = [],
                SkippedFiles = [],
                LoadedAt = DateTimeOffset.UtcNow,
            };
        }

        logger.LogInformation("Loading workspace documents from: {Path}", workspacePath);

        // ── Step 1: load priority documents in declared order ─────────────────
        var priorityNames = _options.WorkspaceDocumentPriority.Count > 0
            ? _options.WorkspaceDocumentPriority
            : WorkspaceDefaults.DefaultPriorityOrder;

        var loadedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var docName in priorityNames)
        {
            var filePath = Path.Combine(workspacePath, $"{docName}.md");
            if (!File.Exists(filePath)) continue;

            var doc = await ReadDocumentAsync(filePath, docName, cancellationToken);
            if (doc is not null)
            {
                docs.Add(doc);
                loadedNames.Add(docName);
            }
        }

        // ── Step 2: load remaining *.md files alphabetically ─────────────────
        var allMdFiles = Directory
            .EnumerateFiles(workspacePath, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var filePath in allMdFiles)
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            if (loadedNames.Contains(name)) continue; // already loaded in priority pass

            var doc = await ReadDocumentAsync(filePath, name, cancellationToken);
            if (doc is not null)
            {
                docs.Add(doc);
                loadedNames.Add(name);
            }
        }

        // ── Step 3: collect skipped non-.md files ─────────────────────────────
        foreach (var filePath in Directory.EnumerateFiles(workspacePath, "*", SearchOption.TopDirectoryOnly))
        {
            if (!filePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                skipped.Add(Path.GetFileName(filePath));
        }

        var result = new WorkspaceLoadResult
        {
            WorkspacePath = workspacePath,
            Documents = docs.AsReadOnly(),
            SkippedFiles = skipped.AsReadOnly(),
            LoadedAt = DateTimeOffset.UtcNow,
        };

        logger.LogInformation(result.Summary);
        if (skipped.Count > 0)
            logger.LogDebug("Skipped non-.md files: {Files}", string.Join(", ", skipped));

        return result;
    }

    private async Task<WorkspaceDocument?> ReadDocumentAsync(
        string filePath,
        string name,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = await File.ReadAllTextAsync(filePath, cancellationToken);
            var fileInfo = new FileInfo(filePath);

            if (string.IsNullOrWhiteSpace(content))
            {
                logger.LogWarning("Workspace document '{Name}' is empty — skipping.", name);
                return null;
            }

            logger.LogDebug("Loaded workspace document: {Name} ({Bytes} bytes)", name, content.Length);

            return new WorkspaceDocument
            {
                Name = name,
                FilePath = filePath,
                Content = content,
                LoadedAt = DateTimeOffset.UtcNow,
                FileModifiedAt = new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero),
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read workspace document: {Path}", filePath);
            return null;
        }
    }

    private string ResolveWorkspacePath()
    {
        var configured = _options.WorkspacePath;

        if (string.IsNullOrWhiteSpace(configured))
            configured = WorkspaceDefaults.FolderName;

        return Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
    }
}

/// <summary>Compile-time constants for workspace defaults.</summary>
public static class WorkspaceDefaults
{
    public const string FolderName = "workspace";

    /// <summary>
    /// The canonical order in which well-known identity documents are injected.
    /// SOUL → AGENTS → USER → CONTEXT gives a natural "who I am → how I work → who you are → current context" flow.
    /// </summary>
    public static readonly List<string> DefaultPriorityOrder =
    [
        "SOUL",
        "AGENTS",
        "USER",
        "CONTEXT",
        "MEMORY",
        "TOOLS",
        "RULES",
    ];
}
