using DotnetClaw.Workspace;
using Spectre.Console;

namespace DotnetClaw.UI;

/// <summary>
/// Abstraction over the terminal output so the agent loop stays testable.
/// </summary>
public interface IConsoleRenderer
{
    void BeginAssistantTurn();
    void WriteChunk(string text);
    void EndAssistantTurn();
    void WriteWarning(string message);
    void WriteToolCall(string toolName, string input);
    void WriteToolResult(string toolName, bool success, string preview);
    void WriteError(string message);
    void WriteBanner();
    void WriteWorkspaceStatus(DotnetClaw.Workspace.WorkspaceLoadResult result);
    string PromptUser(string prompt = "> ");
}

/// <summary>
/// Spectre.Console-powered rich terminal renderer.
/// Inspired by OpenClaw's colour-coded tool-call display.
/// </summary>
public sealed class SpectreConsoleRenderer : IConsoleRenderer
{
    private bool _inAssistantTurn;

    public void WriteBanner()
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(
            new FigletText("DotnetClaw")
                .Centered()
                .Color(Color.Cyan1));

        AnsiConsole.MarkupLine("[bold cyan]  .NET 10 AI Assistant — powered by Microsoft Semantic Kernel[/]");
        AnsiConsole.MarkupLine("[grey]  OpenClaw-inspired agentic loop with Shell, FileSystem & Dotnet skills[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]  Type [white]help[/] for commands, [white]exit[/] to quit, [white]reset[/] to clear context.[/]");
        AnsiConsole.Write(new Rule().RuleStyle("grey"));
        AnsiConsole.WriteLine();
    }

    public void BeginAssistantTurn()
    {
        _inAssistantTurn = true;
        AnsiConsole.Markup("[bold cyan]🦀 DotnetClaw:[/] ");
    }

    public void WriteChunk(string text)
    {
        // Stream tokens directly to stdout without markup interpretation
        Console.Write(text);
    }

    public void EndAssistantTurn()
    {
        if (_inAssistantTurn)
        {
            Console.WriteLine();
            AnsiConsole.WriteLine();
        }
        _inAssistantTurn = false;
    }

    public void WriteWarning(string message)
    {
        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(message)}[/]");
    }

    public void WriteToolCall(string toolName, string input)
    {
        AnsiConsole.WriteLine();
        var panel = new Panel(Markup.Escape(input))
        {
            Header = new PanelHeader($"[bold yellow]⚡ Tool Call: {toolName}[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Yellow),
            Padding = new Padding(1, 0),
        };
        AnsiConsole.Write(panel);
    }

    public void WriteToolResult(string toolName, bool success, string preview)
    {
        var icon = success ? "✅" : "❌";
        var color = success ? "green" : "red";
        var safePreview = Markup.Escape(preview.Length > 300 ? preview[..300] + "…" : preview);
        AnsiConsole.MarkupLine($"[{color}]{icon} {toolName}:[/] {safePreview}");
        AnsiConsole.WriteLine();
    }

    public void WriteError(string message)
    {
        AnsiConsole.MarkupLine($"[bold red]Error:[/] [red]{Markup.Escape(message)}[/]");
    }

    public void WriteWorkspaceStatus(WorkspaceLoadResult result)
    {
        AnsiConsole.WriteLine();
        if (result.IsEmpty)
        {
            AnsiConsole.MarkupLine($"[grey]📂 Workspace: no identity documents found at '{Markup.Escape(result.WorkspacePath)}'[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.Cyan1))
            .AddColumn(new TableColumn("[bold cyan]Document[/]"))
            .AddColumn(new TableColumn("[bold cyan]Modified[/]"))
            .AddColumn(new TableColumn("[bold cyan]Size[/]").RightAligned());

        foreach (var doc in result.Documents)
        {
            table.AddRow(
                $"[white]{Markup.Escape(doc.Name)}[/]",
                $"[grey]{doc.FileModifiedAt:yyyy-MM-dd HH:mm}[/]",
                $"[grey]{doc.Content.Length:N0} chars[/]");
        }

        AnsiConsole.MarkupLine($"[bold cyan]📂 Workspace loaded[/] [grey]({Markup.Escape(result.WorkspacePath)})[/]");
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    public string PromptUser(string prompt = "> ")
    {
        AnsiConsole.Markup($"[bold white]{Markup.Escape(prompt)}[/]");
        return Console.ReadLine() ?? string.Empty;
    }
}
