using System.Text;
using DotnetClaw.UI;
using DotnetClaw.Workspace;

namespace DotnetClaw.Jobby;

/// <summary>
/// No-op <see cref="IConsoleRenderer"/> that captures streamed chunks into a string.
/// Used when a background job runs in isolated mode so its output doesn't
/// appear in the REPL terminal.
/// </summary>
internal sealed class StringCapturingRenderer : IConsoleRenderer
{
    private readonly StringBuilder _sb = new();

    public string GetText() => _sb.ToString().Trim();

    public void BeginAssistantTurn() { }
    public void WriteChunk(string text) => _sb.Append(text);
    public void EndAssistantTurn() { }
    public void WriteWarning(string message) { }
    public void WriteToolCall(string toolName, string input) { }
    public void WriteToolResult(string toolName, bool success, string preview) { }
    public void WriteError(string message) => _sb.AppendLine($"[ERROR] {message}");
    public void WriteBanner() { }
    public void WriteWorkspaceStatus(WorkspaceLoadResult result) { }
    public string PromptUser(string prompt = "> ") => string.Empty;
}
