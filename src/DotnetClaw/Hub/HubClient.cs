using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetClaw.Hub;

// ============================================================================
//  HubClient — HTTP client for DotnetClawHub skills registry
//
//  Supports:
//    • SearchAsync      — query the /api/skills endpoint
//    • DownloadAsync    — fetch raw SKILL.md for a named skill
//    • InstallAsync     — download + save to workspace/skills/{name}/SKILL.md
// ============================================================================

public sealed class HubClient(
    HttpClient http,
    IOptions<HubOptions> options,
    ILogger<HubClient> logger)
{
    private readonly HubOptions _options = options.Value;

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public bool IsEnabled => _options.Enabled;

    /// <summary>
    /// Search for skills on the hub. Pass null or empty string to list all.
    /// </summary>
    public async Task<List<HubSkillInfo>> SearchAsync(
        string? query = null,
        CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(query)
            ? $"{_options.Url}/api/skills"
            : $"{_options.Url}/api/skills?q={Uri.EscapeDataString(query)}";

        try
        {
            var results = await http.GetFromJsonAsync<List<HubSkillInfo>>(url, ct);
            return results ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hub search failed for query '{Query}'", query);
            throw;
        }
    }

    /// <summary>
    /// Download the raw SKILL.md markdown for a named skill.
    /// Returns null when the skill is not found (404).
    /// </summary>
    public async Task<string?> DownloadAsync(string name, CancellationToken ct = default)
    {
        var url = $"{_options.Url}/api/skills/{Uri.EscapeDataString(name)}/SKILL.md";

        try
        {
            var response = await http.GetAsync(url, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Hub download failed for skill '{Name}'", name);
            throw;
        }
    }

    /// <summary>
    /// Download a skill from the hub and save it to
    /// <c>{workspacePath}/skills/{name}/SKILL.md</c>.
    /// Overwrites an existing skill of the same name.
    /// </summary>
    public async Task<HubInstallResult> InstallAsync(
        string name,
        string workspacePath,
        CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return HubInstallResult.Fail(name, "Hub is disabled in configuration.");

        string? markdown;
        try
        {
            markdown = await DownloadAsync(name, ct);
        }
        catch (Exception ex)
        {
            return HubInstallResult.Fail(name, $"Could not reach hub: {ex.Message}");
        }

        if (markdown is null)
            return HubInstallResult.Fail(name, $"Skill '{name}' not found on hub.");

        var skillDir  = Path.Combine(workspacePath, "skills", name);
        var skillFile = Path.Combine(skillDir, "SKILL.md");

        try
        {
            Directory.CreateDirectory(skillDir);
            await File.WriteAllTextAsync(skillFile, markdown, ct);
            logger.LogInformation("Installed skill '{Name}' → {Path}", name, skillFile);
            return HubInstallResult.Ok(name, skillFile);
        }
        catch (Exception ex)
        {
            return HubInstallResult.Fail(name, $"Failed to write skill file: {ex.Message}");
        }
    }
}

/// <summary>Result of a <see cref="HubClient.InstallAsync"/> call.</summary>
public sealed record HubInstallResult(bool Success, string SkillName, string? FilePath, string? Error)
{
    public static HubInstallResult Ok(string name, string path)   => new(true,  name, path, null);
    public static HubInstallResult Fail(string name, string error) => new(false, name, null, error);
}
