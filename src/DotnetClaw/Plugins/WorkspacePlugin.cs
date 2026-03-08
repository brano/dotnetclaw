using System.ComponentModel;
using DotnetClaw.Workspace;
using Microsoft.SemanticKernel;

namespace DotnetClaw.Plugins;

/// <summary>
/// Runtime workspace skill.
/// Lets the agent inspect, reload, and query identity documents mid-conversation.
/// </summary>
public sealed class WorkspacePlugin(WorkspaceLoader loader)
{
    [KernelFunction("list_workspace_docs")]
    [Description(
        "List all identity documents currently loaded from the workspace folder. " +
        "Returns names, file paths, and last-modified timestamps.")]
    public async Task<string> ListWorkspaceDocsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await loader.LoadAsync(cancellationToken);

        if (result.IsEmpty)
            return $"No workspace documents found at '{result.WorkspacePath}'.";

        var lines = result.Documents.Select(d =>
            $"• {d.Name,-20} | Modified: {d.FileModifiedAt:yyyy-MM-dd HH:mm}  | {d.FilePath}");

        return $"""
                Workspace: {result.WorkspacePath}
                Loaded   : {result.LoadedAt:yyyy-MM-dd HH:mm:ss} UTC

                {string.Join("\n", lines)}
                """;
    }

    [KernelFunction("get_workspace_doc")]
    [Description(
        "Retrieve the full content of a specific workspace document by name. " +
        "For example: 'SOUL', 'USER', 'AGENTS'. Name is case-insensitive.")]
    public async Task<string> GetWorkspaceDocAsync(
        [Description("Document name without extension, e.g. 'SOUL' or 'USER'")]
        string name,
        CancellationToken cancellationToken = default)
    {
        var doc = await loader.GetDocumentAsync(name, cancellationToken);

        if (doc is null)
            return $"[NOT FOUND] No workspace document named '{name}'. Use list_workspace_docs to see available documents.";

        return $"""
                ── {doc.Name} ─────────────────────────────────────────
                File    : {doc.FilePath}
                Modified: {doc.FileModifiedAt:yyyy-MM-dd HH:mm:ss} UTC
                ──────────────────────────────────────────────────────

                {doc.Content}
                """;
    }

    [KernelFunction("reload_workspace")]
    [Description(
        "Force a reload of all workspace documents from disk. " +
        "Use this if you know files have changed during the current session.")]
    public async Task<string> ReloadWorkspaceAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await loader.ReloadAsync(cancellationToken);
        return $"[OK] Workspace reloaded. {result.Summary}";
    }

    [KernelFunction("get_workspace_context")]
    [Description(
        "Return the full combined context block of all workspace documents, " +
        "exactly as it was injected into the system prompt at session start.")]
    public async Task<string> GetWorkspaceContextAsync(
        CancellationToken cancellationToken = default)
    {
        var block = await loader.BuildContextBlockAsync(cancellationToken);
        return string.IsNullOrEmpty(block)
            ? "Workspace is empty — no identity documents were loaded."
            : block;
    }

    [KernelFunction("list_skills")]
    [Description(
        "List all skills loaded from the workspace/skills/ directory. " +
        "Each skill is a how-to guide for a specific capability (e.g. weather, github). " +
        "Returns skill names, file paths, and last-modified timestamps.")]
    public async Task<string> ListSkillsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await loader.LoadAsync(cancellationToken);

        if (result.Skills.Count == 0)
            return $"No skills found in '{result.WorkspacePath}/skills/'.";

        var lines = result.Skills.Select(s =>
            $"• {s.SkillName,-25} | Modified: {s.FileModifiedAt:yyyy-MM-dd HH:mm}  | {s.FilePath}");

        return $"""
                Skills directory: {result.WorkspacePath}/skills/
                Loaded          : {result.LoadedAt:yyyy-MM-dd HH:mm:ss} UTC

                {string.Join("\n", lines)}
                """;
    }

    [KernelFunction("get_skill")]
    [Description(
        "Retrieve the full SKILL.md content for a specific skill by name. " +
        "For example: 'weather', 'github', 'copilot-cli'. Name is case-insensitive.")]
    public async Task<string> GetSkillAsync(
        [Description("Skill folder name, e.g. 'weather' or 'github'")]
        string skillName,
        CancellationToken cancellationToken = default)
    {
        var skill = await loader.GetSkillAsync(skillName, cancellationToken);

        if (skill is null)
        {
            var result = await loader.LoadAsync(cancellationToken);
            var available = result.Skills.Count > 0
                ? string.Join(", ", result.Skills.Select(s => s.SkillName))
                : "none";
            return $"[NOT FOUND] No skill named '{skillName}'. Available skills: {available}.";
        }

        return $"""
                ── SKILL: {skill.SkillName} ─────────────────────────────────────────
                File    : {skill.FilePath}
                Modified: {skill.FileModifiedAt:yyyy-MM-dd HH:mm:ss} UTC
                ──────────────────────────────────────────────────────────────────────

                {skill.Content}
                """;
    }
}
