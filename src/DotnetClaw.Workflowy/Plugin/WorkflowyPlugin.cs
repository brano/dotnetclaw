using System.ComponentModel;
using System.Text.Json;
using DotnetClaw.Workflowy.Engine;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace DotnetClaw.Workflowy.Plugin;

/// <summary>
/// Semantic Kernel plugin that exposes Workflowy workflow execution to the DotnetClaw agent.
///
/// Tool call protocol:
///   run_workflow    — {"action":"run","pipeline":"/path/to.yaml --arg val","timeoutMs":30000}
///   resume_workflow — {"action":"resume","token":"&lt;token&gt;","approve":true}
///
/// Both functions return a serialized WorkflowyResponse JSON envelope.
/// </summary>
public sealed class WorkflowyPlugin(
    WorkflowEngine engine,
    IApprovalNotifier notifier,
    ILogger<WorkflowyPlugin> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    [KernelFunction("run_workflow")]
    [Description(
        "Run a Workflowy deterministic workflow file. " +
        "Pass requestJson as JSON: {\"action\":\"run\",\"pipeline\":\"/path/to/workflow.yaml --argName value\",\"timeoutMs\":30000}. " +
        "Returns a JSON envelope: {ok, status, output, requiresApproval}. " +
        "If status is 'needs_approval', present the approval prompt to the user then call resume_workflow with the resumeToken.")]
    public async Task<string> RunWorkflowAsync(
        [Description("JSON object: {action:'run', pipeline:'/path/to.yaml --arg value', timeoutMs:30000}")]
        string requestJson,
        CancellationToken cancellationToken = default)
    {
        WorkflowyRequest? req;
        try { req = JsonSerializer.Deserialize<WorkflowyRequest>(requestJson, JsonOpts); }
        catch (Exception ex)
        {
            return Serialize(WorkflowyResponse.Failure($"Invalid request JSON: {ex.Message}"));
        }

        if (req is null || !string.Equals(req.Action, "run", StringComparison.OrdinalIgnoreCase))
            return Serialize(WorkflowyResponse.Failure("action must be 'run'"));

        if (string.IsNullOrWhiteSpace(req.Pipeline))
            return Serialize(WorkflowyResponse.Failure("pipeline is required"));

        var (workflowPath, args) = ParsePipelineString(req.Pipeline);

        WorkflowyResponse response;
        try
        {
            response = await engine.RunAsync(workflowPath, args, req.TimeoutMs, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Workflow engine error running {Path}", workflowPath);
            response = WorkflowyResponse.Failure($"Engine error: {ex.Message}");
        }

        if (response.Status == "needs_approval" && response.RequiresApproval is not null)
        {
            var dto = new PendingApprovalDto(
                response.RequiresApproval.ResumeToken,
                Path.GetFileNameWithoutExtension(workflowPath),
                response.RequiresApproval.Prompt,
                response.RequiresApproval.Items,
                DateTimeOffset.UtcNow);
            await notifier.NotifyPendingAsync(dto, cancellationToken);
        }

        return Serialize(response);
    }

    [KernelFunction("resume_workflow")]
    [Description(
        "Resume a Workflowy workflow that is waiting for human approval. " +
        "Pass requestJson as JSON: {\"action\":\"resume\",\"token\":\"<resumeToken>\",\"approve\":true}. " +
        "Set approve to false to reject/cancel the workflow. " +
        "Returns a JSON envelope with the final workflow status.")]
    public async Task<string> ResumeWorkflowAsync(
        [Description("JSON object: {action:'resume', token:'<resumeToken>', approve:true}")]
        string requestJson,
        CancellationToken cancellationToken = default)
    {
        WorkflowyRequest? req;
        try { req = JsonSerializer.Deserialize<WorkflowyRequest>(requestJson, JsonOpts); }
        catch (Exception ex)
        {
            return Serialize(WorkflowyResponse.Failure($"Invalid request JSON: {ex.Message}"));
        }

        if (req is null || !string.Equals(req.Action, "resume", StringComparison.OrdinalIgnoreCase))
            return Serialize(WorkflowyResponse.Failure("action must be 'resume'"));

        if (string.IsNullOrWhiteSpace(req.Token))
            return Serialize(WorkflowyResponse.Failure("token is required"));

        WorkflowyResponse response;
        try
        {
            response = await engine.ResumeAsync(req.Token, req.Approve, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resume error for token {Token}", req.Token);
            response = WorkflowyResponse.Failure($"Resume error: {ex.Message}");
        }

        await notifier.NotifyResumedAsync(req.Token, response.Status, cancellationToken);
        return Serialize(response);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static string Serialize(WorkflowyResponse r) =>
        JsonSerializer.Serialize(r, JsonOpts);

    /// <summary>
    /// Parses a pipeline string into a workflow path and key-value args.
    /// Format: "/path/to/workflow.yaml --key1 value1 --key2 value2"
    /// </summary>
    private static (string Path, Dictionary<string, string> Args) ParsePipelineString(string pipeline)
    {
        var parts = pipeline.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var path = parts[0];
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i + 1 < parts.Length; i += 2)
            args[parts[i].TrimStart('-')] = parts[i + 1];

        return (path, args);
    }
}
