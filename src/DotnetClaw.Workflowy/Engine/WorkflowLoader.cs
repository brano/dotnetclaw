using System.Text.Json;
using DotnetClaw.Workflowy.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DotnetClaw.Workflowy.Engine;

/// <summary>
/// Loads and validates workflow files from YAML (.yaml/.yml) or JSON (.json) format.
/// </summary>
public sealed class WorkflowLoader
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Loads and parses a workflow file from <paramref name="path"/>.</summary>
    public WorkflowFile Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Workflow file not found: {path}", path);

        var content = File.ReadAllText(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return LoadFromString(content, ext == ".json" ? "json" : "yaml");
    }

    /// <summary>Parses a workflow from a string. <paramref name="format"/> is "yaml" or "json".</summary>
    public WorkflowFile LoadFromString(string content, string format = "yaml")
    {
        WorkflowFile? workflow = format == "json"
            ? JsonSerializer.Deserialize<WorkflowFile>(content, JsonOptions)
            : YamlDeserializer.Deserialize<WorkflowFile>(content);

        return workflow ?? throw new InvalidOperationException("Workflow deserialization returned null.");
    }

    /// <summary>Returns a list of validation errors. Empty list means valid.</summary>
    public IReadOnlyList<string> Validate(WorkflowFile workflow)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(workflow.Name))
            errors.Add("Workflow 'name' is required.");

        if (workflow.Steps.Count == 0)
            errors.Add("Workflow must have at least one step.");

        // Check for duplicate step names
        var namedSteps = workflow.Steps
            .Where(s => !string.IsNullOrEmpty(s.Name))
            .Select(s => s.Name!)
            .ToList();

        foreach (var dup in namedSteps.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key))
            errors.Add($"Duplicate step name: '{dup}'.");

        // Each step must have exactly one action
        for (var i = 0; i < workflow.Steps.Count; i++)
        {
            var step = workflow.Steps[i];
            var label = step.Name is not null ? $"'{step.Name}'" : $"#{i}";
            var count = (step.EffectiveRun is not null ? 1 : 0)
                      + (step.Pipeline is not null ? 1 : 0)
                      + (step.Approval is not null ? 1 : 0);

            if (count == 0)
                errors.Add($"Step {label} has no action (expected run/command/pipeline/approval).");
            else if (count > 1)
                errors.Add($"Step {label} has multiple actions — use exactly one.");
        }

        return errors;
    }
}
