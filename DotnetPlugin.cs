using System.ComponentModel;
using System.Text;
using Microsoft.SemanticKernel;

namespace DotnetClaw.Plugins;

/// <summary>
/// Code-aware skill for analysing and working with .NET / C# projects.
/// Helps the agent understand project structure without running the compiler.
/// </summary>
public sealed class DotnetPlugin
{
    [KernelFunction("find_csharp_projects")]
    [Description("Locate all .csproj files under a given directory.")]
    public Task<string> FindProjectsAsync(
        [Description("Root directory to search. Defaults to current directory.")]
        string? directory = null)
    {
        var root = directory ?? Directory.GetCurrentDirectory();
        if (!Directory.Exists(root))
            return Task.FromResult($"[ERROR] Directory not found: {root}");

        var projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p))
            .ToList();

        if (projects.Count == 0)
            return Task.FromResult("No .csproj files found.");

        return Task.FromResult(string.Join("\n", projects));
    }

    [KernelFunction("summarise_csharp_file")]
    [Description(
        "Return a structural summary of a C# file: namespaces, types, methods, " +
        "and properties — without returning the full source.")]
    public async Task<string> SummariseFileAsync(
        [Description("Path to the .cs file")]
        string path)
    {
        if (!File.Exists(path))
            return $"[ERROR] File not found: {path}";

        var lines = await File.ReadAllLinesAsync(path);
        var sb = new StringBuilder();
        sb.AppendLine($"File: {Path.GetFileName(path)}  ({lines.Length} lines)");

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Rough heuristic scan (no Roslyn dependency in skeleton)
            if (trimmed.StartsWith("namespace "))
                sb.AppendLine($"  namespace {trimmed["namespace ".Length..].TrimEnd('{', ' ')}");
            else if (IsTypeDeclaration(trimmed, out var typeKind, out var typeName))
                sb.AppendLine($"    {typeKind}: {typeName}");
            else if (IsMethodOrProperty(trimmed, out var memberInfo))
                sb.AppendLine($"      member: {memberInfo}");
        }

        return sb.ToString();
    }

    [KernelFunction("get_nuget_packages")]
    [Description("List all NuGet package references in a .csproj file.")]
    public async Task<string> GetNugetPackagesAsync(
        [Description("Path to the .csproj file")]
        string csprojPath)
    {
        if (!File.Exists(csprojPath))
            return $"[ERROR] File not found: {csprojPath}";

        var content = await File.ReadAllTextAsync(csprojPath);
        var packages = new List<string>();
        var lines = content.Split('\n');

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("<PackageReference"))
            {
                var include = ExtractAttribute(trimmed, "Include");
                var version = ExtractAttribute(trimmed, "Version");
                if (include is not null)
                    packages.Add($"{include} {version ?? "(no version)"}");
            }
        }

        return packages.Count == 0
            ? "No PackageReference entries found."
            : string.Join("\n", packages);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static bool IsTypeDeclaration(string line, out string kind, out string name)
    {
        kind = name = string.Empty;
        foreach (var kw in new[] { "class ", "interface ", "record ", "struct ", "enum " })
        {
            var idx = line.IndexOf(kw, StringComparison.Ordinal);
            if (idx >= 0)
            {
                kind = kw.Trim();
                name = line[(idx + kw.Length)..].Split([' ', '{', '(', ':'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                return true;
            }
        }
        return false;
    }

    private static bool IsMethodOrProperty(string line, out string info)
    {
        info = string.Empty;
        if (!line.Contains('(') && !line.Contains("{ get") && !line.Contains("{ set"))
            return false;
        if (line.StartsWith("//") || line.StartsWith("*"))
            return false;

        info = line.Length > 80 ? line[..80] + "…" : line;
        return true;
    }

    private static string? ExtractAttribute(string xml, string attr)
    {
        var key = $"{attr}=\"";
        var start = xml.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += key.Length;
        var end = xml.IndexOf('"', start);
        return end < 0 ? null : xml[start..end];
    }
}
