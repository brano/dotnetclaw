using DotnetClaw.Config;
using DotnetClaw.Mcp;
using DotnetClaw.UI;
using DotnetClaw.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

namespace DotnetClaw.Agents;

/// <summary>
/// The core agentic loop for DotnetClaw.
///
/// Architecture mirrors OpenClaw's tool-use loop:
///   1. Session starts → workspace identity documents are loaded and prepended
///      to the system prompt (SOUL.md → AGENTS.md → USER.md → custom docs)
///   2. User sends a message
///   3. Agent decides which tools (plugins) to call
///   4. Tool results are fed back into context
///   5. Loop repeats until the agent produces a final text response
///      OR the max-iteration guard fires
///
/// Uses Microsoft Semantic Kernel's <see cref="ChatCompletionAgent"/> as the
/// agent runtime, with automatic function calling enabled.
/// </summary>
public sealed class ClawAgentLoop(
    Kernel kernel,
    WorkspaceLoader workspaceLoader,
    McpKernelLoader mcpKernelLoader,
    IOptions<DotnetClawOptions> options,
    IConsoleRenderer renderer,
    ILogger<ClawAgentLoop> logger)
{
    private readonly DotnetClawOptions _options = options.Value;

    // -------------------------------------------------------------------------
    // Session state
    // -------------------------------------------------------------------------

    private readonly ChatHistory _history = [];
    private int _totalIterations;
    private string _effectiveSystemPrompt = string.Empty;

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Asynchronously initialise the agent.
    /// Loads workspace identity documents and constructs the full system prompt.
    /// Must be awaited once before the first call to <see cref="RunTurnAsync"/>.
    /// </summary>
    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        // Load MCP server tools into the kernel as Mcp_{name} plugins
        await mcpKernelLoader.LoadAsync(kernel, cancellationToken);

        _effectiveSystemPrompt = await BuildSystemPromptAsync(cancellationToken);
        _history.Clear();
        _history.AddSystemMessage(_effectiveSystemPrompt);
        logger.LogInformation(
            "ClawAgentLoop initialised. MaxIterations={Max} SystemPromptLength={Len}",
            _options.MaxIterations,
            _effectiveSystemPrompt.Length);
    }

    /// <summary>
    /// Process a single user message through the full agentic loop.
    /// </summary>
    /// <param name="userMessage">The user's input.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="outputSink">
    /// Optional alternative renderer — if supplied, token output goes here instead of the
    /// terminal. Used by the Telegram integration to capture responses as strings.
    /// </param>
    public async Task RunTurnAsync(
        string userMessage,
        CancellationToken cancellationToken = default,
        UI.IConsoleRenderer? outputSink = null)
    {
        // Use the provided sink, or fall back to the injected terminal renderer
        var sink = outputSink ?? renderer;

        _history.AddUserMessage(userMessage);

        var agent = BuildAgent();

        int iterations = 0;
        string lastAssistantText = string.Empty;

        sink.BeginAssistantTurn();

        // ── Agentic loop ─────────────────────────────────────────────────────
        while (iterations < _options.MaxIterations)
        {
            iterations++;
            _totalIterations++;
            logger.LogDebug("Agent iteration {I} / {Max}", iterations, _options.MaxIterations);

            await foreach (var streamChunk in agent.InvokeStreamingAsync(
                               new ChatMessageContent(AuthorRole.User, userMessage),
                               cancellationToken: cancellationToken))
            {
                var chunkContent = streamChunk.Message.Content;
                if (chunkContent is not null)
                {
                    sink.WriteChunk(chunkContent);
                    lastAssistantText += chunkContent;
                }
            }

            // A text response means the agent has finished tool calls — exit
            if (!string.IsNullOrWhiteSpace(lastAssistantText))
                break;

            if (iterations >= _options.MaxIterations)
            {
                sink.WriteWarning(
                    $"\n[DotnetClaw] Reached max iterations ({_options.MaxIterations}). Stopping.");
                break;
            }
        }
        // ── End of loop ──────────────────────────────────────────────────────

        sink.EndAssistantTurn();

        if (!string.IsNullOrWhiteSpace(lastAssistantText))
            _history.AddAssistantMessage(lastAssistantText);

        logger.LogInformation("Turn completed in {I} iterations", iterations);
    }

    /// <summary>
    /// Reset conversation history.
    /// If <see cref="DotnetClawOptions.ReloadWorkspaceOnReset"/> is true, workspace
    /// documents are re-read from disk before the new session starts.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        if (_options.ReloadWorkspaceOnReset)
        {
            var result = await workspaceLoader.ReloadAsync(cancellationToken);
            renderer.WriteWorkspaceStatus(result);
        }

        await InitialiseAsync(cancellationToken);
        logger.LogInformation("Conversation history cleared and workspace reloaded.");
    }

    /// <summary>Return a snapshot of the current chat history.</summary>
    public IReadOnlyList<ChatMessageContent> GetHistory() => [.. _history];

    /// <summary>Expose the effective (workspace-enriched) system prompt.</summary>
    public string EffectiveSystemPrompt => _effectiveSystemPrompt;

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds the full system prompt by composing:
    ///   [base system prompt from appsettings]
    ///   +
    ///   [workspace context block with all identity documents]
    /// </summary>
    private async Task<string> BuildSystemPromptAsync(CancellationToken cancellationToken)
    {
        var basePrompt = _options.SystemPrompt.Trim();
        var workspaceBlock = await workspaceLoader.BuildContextBlockAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(workspaceBlock))
            return basePrompt;

        return $"""
                {basePrompt}

                {workspaceBlock}
                """;
    }

    private ChatCompletionAgent BuildAgent() => new()
    {
        Name = "DotnetClaw",
        Instructions = _effectiveSystemPrompt,
        Kernel = kernel,
        Arguments = new KernelArguments(
            new PromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
            }),
    };
}

/// <summary>Placeholder for future streaming response type.</summary>
internal sealed class AgentResponse;
