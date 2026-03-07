using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DotnetClaw.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace DotnetClaw.Plugins;

/// <summary>
/// Shell / CLI skill that gives the agent the ability to run terminal commands.
/// Inspired by OpenClaw's bash tool — wraps process execution with safety guards.
/// </summary>
public sealed class ShellPlugin(
    IOptions<DotnetClawOptions> options,
    ILogger<ShellPlugin> logger)
{
    private readonly DotnetClawOptions _options = options.Value;

    // -------------------------------------------------------------------------
    // Public kernel functions
    // -------------------------------------------------------------------------

    /// <summary>
    /// Execute a shell command and return its stdout + stderr.
    /// </summary>
    [KernelFunction("run_command")]
    [Description(
        "Run a shell command in the working directory. " +
        "Returns the combined stdout and stderr output along with the exit code. " +
        "Use this for running scripts, build tools, git commands, and CLI utilities.")]
    public async Task<ShellResult> RunCommandAsync(
        [Description("The full shell command to execute, e.g. 'dotnet build' or 'git status'")]
        string command,
        [Description("Optional: override the working directory for this command")]
        string? workingDirectory = null,
        [Description("Timeout in seconds. Default 30, max 300.")]
        int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        var effectiveDir = ResolveDirectory(workingDirectory);
        var guardResult = CheckCommandSafety(command);
        if (guardResult.IsAllowed != CommandSafetyResult.Allow)
        {
            logger.LogWarning("Blocked shell command: {Command} — reason: {Reason}", command, guardResult.Reason);
            return ShellResult.Blocked(command, guardResult.Reason);
        }

        logger.LogInformation("Executing command: {Command} in {Dir}", command, effectiveDir);

        var (fileName, args) = ParseCommand(command);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            WorkingDirectory = effectiveDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutSb.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrSb.AppendLine(e.Data); };

        var capped = Math.Clamp(timeoutSeconds, 1, 300);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(capped));

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cts.Token);

            var result = new ShellResult
            {
                Command = command,
                ExitCode = process.ExitCode,
                Stdout = stdoutSb.ToString().TrimEnd(),
                Stderr = stderrSb.ToString().TrimEnd(),
                WorkingDirectory = effectiveDir,
                Success = process.ExitCode == 0,
            };

            logger.LogInformation("Command exited with code {Code}", result.ExitCode);
            return result;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return ShellResult.Timeout(command, capped);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute command: {Command}", command);
            return ShellResult.Error(command, ex.Message);
        }
    }

    /// <summary>
    /// List files and directories in a path.
    /// </summary>
    [KernelFunction("list_directory")]
    [Description("List files and directories at the specified path. Returns a tree-style listing.")]
    public Task<string> ListDirectoryAsync(
        [Description("Path to list. Defaults to current working directory if empty.")]
        string? path = null)
    {
        var dir = ResolveDirectory(path);
        if (!Directory.Exists(dir))
            return Task.FromResult($"Directory not found: {dir}");

        var sb = new StringBuilder();
        sb.AppendLine($"📁 {dir}");
        AppendDirectoryTree(sb, dir, "", maxDepth: 3, currentDepth: 0);
        return Task.FromResult(sb.ToString());
    }

    /// <summary>
    /// Get the current working directory.
    /// </summary>
    [KernelFunction("get_working_directory")]
    [Description("Returns the current working directory path.")]
    public Task<string> GetWorkingDirectoryAsync()
        => Task.FromResult(ResolveDirectory(null));

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private string ResolveDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Path.GetFullPath(_options.WorkingDirectory);
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(_options.WorkingDirectory, path));
    }

    private (CommandSafetyResult IsAllowed, string Reason) CheckCommandSafety(string command)
    {
        // Check block-list first
        foreach (var blocked in _options.BlockedShellCommands)
        {
            if (command.Contains(blocked, StringComparison.OrdinalIgnoreCase))
                return (CommandSafetyResult.Deny, $"Matches blocked pattern: '{blocked}'");
        }

        // If allow-list is empty, allow everything (dev mode)
        if (_options.AllowedShellCommands.Count == 0)
            return (CommandSafetyResult.Allow, string.Empty);

        // Check allow-list prefix match
        var cmd = command.TrimStart();
        foreach (var allowed in _options.AllowedShellCommands)
        {
            if (cmd.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                return (CommandSafetyResult.Allow, string.Empty);
        }

        return (CommandSafetyResult.Deny, "Command not in the allow-list.");
    }

    private static (string FileName, string Args) ParseCommand(string command)
    {
        var parts = command.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => ("echo", ""),
            1 => (parts[0], ""),
            _ => (parts[0], parts[1]),
        };
    }

    private static void AppendDirectoryTree(StringBuilder sb, string path, string indent, int maxDepth, int currentDepth)
    {
        if (currentDepth >= maxDepth) return;

        try
        {
            var entries = Directory.GetFileSystemEntries(path).Take(50).ToArray();
            foreach (var (entry, idx) in entries.Select((e, i) => (e, i)))
            {
                bool isLast = idx == entries.Length - 1;
                var prefix = indent + (isLast ? "└── " : "├── ");
                var name = Path.GetFileName(entry);

                if (Directory.Exists(entry))
                {
                    sb.AppendLine($"{prefix}📁 {name}/");
                    AppendDirectoryTree(sb, entry, indent + (isLast ? "    " : "│   "), maxDepth, currentDepth + 1);
                }
                else
                {
                    sb.AppendLine($"{prefix}📄 {name}");
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            sb.AppendLine($"{indent}  [access denied]");
        }
    }
}

// -------------------------------------------------------------------------
// Result / guard types
// -------------------------------------------------------------------------

public enum CommandSafetyResult { Allow, Deny }

/// <summary>Structured result returned from a shell command execution.</summary>
public sealed class ShellResult
{
    public string Command { get; init; } = string.Empty;
    public int ExitCode { get; init; }
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }

    public override string ToString()
    {
        var parts = new List<string>
        {
            $"Command : {Command}",
            $"ExitCode: {ExitCode}",
            $"Success : {Success}",
        };
        if (!string.IsNullOrWhiteSpace(Stdout)) parts.Add($"Stdout:\n{Stdout}");
        if (!string.IsNullOrWhiteSpace(Stderr)) parts.Add($"Stderr:\n{Stderr}");
        if (!string.IsNullOrWhiteSpace(ErrorMessage)) parts.Add($"Error  : {ErrorMessage}");
        return string.Join("\n", parts);
    }

    public static ShellResult Blocked(string cmd, string reason) => new()
    {
        Command = cmd, ExitCode = -1, Success = false,
        ErrorMessage = $"[BLOCKED] {reason}"
    };

    public static ShellResult Timeout(string cmd, int timeoutSecs) => new()
    {
        Command = cmd, ExitCode = -2, Success = false,
        ErrorMessage = $"[TIMEOUT] Command exceeded {timeoutSecs}s limit."
    };

    public static ShellResult Error(string cmd, string message) => new()
    {
        Command = cmd, ExitCode = -3, Success = false,
        ErrorMessage = $"[ERROR] {message}"
    };
}
