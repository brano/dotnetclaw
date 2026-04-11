using System.Text.RegularExpressions;
using DotnetClaw.Workflowy.Models;

namespace DotnetClaw.Workflowy.Engine;

/// <summary>
/// Resolves {{variable}} tokens in workflow step templates.
///
/// Variable namespaces:
///   {{args.name}}        — argument supplied at run-time
///   {{env.VAR_NAME}}     — environment variable declared in the workflow's env: block
///   {{stepname.stdout}}  — stdout from a completed named step
///   {{stepname.stderr}}  — stderr from a completed named step
///   {{stepname.exitCode}} — exit code from a completed named step
///   {{stepname.success}} — "true"/"false" based on exit code
///
/// Unknown tokens are left unchanged to avoid accidental data loss.
/// </summary>
public sealed class VariableResolver
{
    private static readonly Regex TokenPattern = new(@"\{\{([^}]+)\}\}", RegexOptions.Compiled);

    /// <summary>Substitutes all {{token}} placeholders in <paramref name="template"/> from <paramref name="context"/>.</summary>
    public string Resolve(string template, IReadOnlyDictionary<string, string> context)
    {
        return TokenPattern.Replace(template, m =>
        {
            var key = m.Groups[1].Value.Trim();
            return context.TryGetValue(key, out var val) ? val : m.Value;
        });
    }

    /// <summary>Builds the initial interpolation context from workflow env and supplied args.</summary>
    public Dictionary<string, string> BuildInitialContext(
        WorkflowFile workflow,
        IReadOnlyDictionary<string, string> suppliedArgs)
    {
        var ctx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (k, v) in workflow.Env)
            ctx[$"env.{k}"] = v;

        foreach (var (k, v) in suppliedArgs)
            ctx[$"args.{k}"] = v;

        return ctx;
    }

    /// <summary>Adds step output entries to context after a step completes.</summary>
    public void AddStepOutputs(Dictionary<string, string> context, string stepName, StepResult result)
    {
        if (string.IsNullOrEmpty(stepName)) return;
        context[$"{stepName}.stdout"] = result.Stdout;
        context[$"{stepName}.stderr"] = result.Stderr;
        context[$"{stepName}.exitCode"] = result.ExitCode.ToString();
        context[$"{stepName}.success"] = (result.ExitCode == 0).ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Evaluates a condition expression after resolving variables.
    /// Supports: "{{token}} == value", "{{token}} != value", "true/false/yes/no/1/0".
    /// </summary>
    public bool EvaluateCondition(string condition, IReadOnlyDictionary<string, string> context)
    {
        var resolved = Resolve(condition, context).Trim();

        var eqIdx = resolved.IndexOf("==", StringComparison.Ordinal);
        if (eqIdx >= 0)
        {
            var left = resolved[..eqIdx].Trim();
            var right = resolved[(eqIdx + 2)..].Trim();
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        var neIdx = resolved.IndexOf("!=", StringComparison.Ordinal);
        if (neIdx >= 0)
        {
            var left = resolved[..neIdx].Trim();
            var right = resolved[(neIdx + 2)..].Trim();
            return !string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        return resolved is "true" or "1" or "yes";
    }
}
