using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace DotnetClaw.Plugins;

/// <summary>
/// File system skill — read, write, and manage files on disk.
/// Complements ShellPlugin for code-generation and editing workflows.
/// </summary>
public sealed class FileSystemPlugin(ILogger<FileSystemPlugin> logger)
{
    private const int MaxReadBytes = 1_048_576; // 1 MB safety cap

    [KernelFunction("read_file")]
    [Description("Read the contents of a text file. Returns the file content as a string.")]
    public async Task<string> ReadFileAsync(
        [Description("Absolute or relative path to the file to read")]
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return $"[ERROR] File not found: {path}";

        var info = new FileInfo(path);
        if (info.Length > MaxReadBytes)
            return $"[ERROR] File too large ({info.Length:N0} bytes). Max is {MaxReadBytes:N0} bytes.";

        logger.LogInformation("Reading file: {Path}", path);
        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    [KernelFunction("write_file")]
    [Description("Write content to a file, creating it or overwriting if it already exists.")]
    public async Task<string> WriteFileAsync(
        [Description("Absolute or relative path to write to")]
        string path,
        [Description("Full content to write to the file")]
        string content,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(path, content, cancellationToken);
            logger.LogInformation("Wrote {Bytes} bytes to: {Path}", content.Length, path);
            return $"[OK] Written {content.Length:N0} characters to {path}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write file: {Path}", path);
            return $"[ERROR] {ex.Message}";
        }
    }

    [KernelFunction("append_file")]
    [Description("Append content to an existing file, or create it if it does not exist.")]
    public async Task<string> AppendFileAsync(
        [Description("Absolute or relative path to the file")]
        string path,
        [Description("Content to append")]
        string content,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await File.AppendAllTextAsync(path, content, cancellationToken);
            return $"[OK] Appended {content.Length:N0} characters to {path}";
        }
        catch (Exception ex)
        {
            return $"[ERROR] {ex.Message}";
        }
    }

    [KernelFunction("delete_file")]
    [Description("Delete a file from disk. Returns confirmation or error.")]
    public Task<string> DeleteFileAsync(
        [Description("Absolute or relative path to delete")]
        string path)
    {
        if (!File.Exists(path))
            return Task.FromResult($"[WARN] File not found: {path}");

        try
        {
            File.Delete(path);
            logger.LogInformation("Deleted file: {Path}", path);
            return Task.FromResult($"[OK] Deleted: {path}");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"[ERROR] {ex.Message}");
        }
    }

    [KernelFunction("file_exists")]
    [Description("Check whether a file or directory exists at the given path.")]
    public Task<string> FileExistsAsync(
        [Description("Path to check")]
        string path)
    {
        bool fileExists = File.Exists(path);
        bool dirExists = Directory.Exists(path);

        if (fileExists) return Task.FromResult($"[EXISTS] File: {path}");
        if (dirExists) return Task.FromResult($"[EXISTS] Directory: {path}");
        return Task.FromResult($"[NOT FOUND] {path}");
    }

    [KernelFunction("find_files")]
    [Description("Search for files matching a glob pattern within a directory.")]
    public Task<string> FindFilesAsync(
        [Description("Root directory to search from")]
        string directory,
        [Description("Glob search pattern, e.g. '*.cs' or '**/*.json'")]
        string pattern,
        [Description("Max number of results to return")]
        int maxResults = 50)
    {
        if (!Directory.Exists(directory))
            return Task.FromResult($"[ERROR] Directory not found: {directory}");

        var files = Directory
            .EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
            .Take(maxResults)
            .Select(f => Path.GetRelativePath(directory, f))
            .ToList();

        if (files.Count == 0)
            return Task.FromResult($"No files matching '{pattern}' in {directory}");

        return Task.FromResult(string.Join("\n", files));
    }
}
