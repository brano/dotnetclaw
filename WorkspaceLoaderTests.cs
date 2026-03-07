using DotnetClaw.Config;
using DotnetClaw.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotnetClaw.Tests;

public class WorkspaceLoaderTests : IDisposable
{
    private readonly string _tempWorkspace =
        Path.Combine(Path.GetTempPath(), $"dotnetclaw-ws-{Guid.NewGuid():N}");

    public WorkspaceLoaderTests() => Directory.CreateDirectory(_tempWorkspace);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private WorkspaceLoader CreateLoader(string? path = null)
    {
        var opts = Options.Create(new DotnetClawOptions
        {
            WorkspacePath = path ?? _tempWorkspace,
            WorkspaceDocumentPriority = ["SOUL", "AGENTS", "USER"],
        });
        return new WorkspaceLoader(opts, NullLogger<WorkspaceLoader>.Instance);
    }

    private async Task WriteDocAsync(string name, string content)
        => await File.WriteAllTextAsync(Path.Combine(_tempWorkspace, $"{name}.md"), content);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_EmptyFolder_ReturnsEmptyResult()
    {
        var loader = CreateLoader();
        var result = await loader.LoadAsync();

        Assert.True(result.IsEmpty);
        Assert.Empty(result.Documents);
    }

    [Fact]
    public async Task LoadAsync_NonExistentFolder_ReturnsEmptyWithoutThrowing()
    {
        var loader = CreateLoader("/this/path/does/not/exist/ever");
        var result = await loader.LoadAsync();

        Assert.True(result.IsEmpty);
    }

    [Fact]
    public async Task LoadAsync_PriorityDocumentsLoadedFirst()
    {
        await WriteDocAsync("SOUL", "# Soul content");
        await WriteDocAsync("USER", "# User content");
        await WriteDocAsync("AGENTS", "# Agents content");
        await WriteDocAsync("ZEBRA", "# Custom doc");

        var loader = CreateLoader();
        var result = await loader.LoadAsync();

        Assert.Equal(4, result.Documents.Count);
        Assert.Equal("SOUL", result.Documents[0].Name);
        Assert.Equal("AGENTS", result.Documents[1].Name);
        Assert.Equal("USER", result.Documents[2].Name);
        Assert.Equal("ZEBRA", result.Documents[3].Name); // alphabetical after priority
    }

    [Fact]
    public async Task LoadAsync_DocumentContainsCorrectContent()
    {
        const string expected = "# My Soul\nI am a helpful assistant.";
        await WriteDocAsync("SOUL", expected);

        var loader = CreateLoader();
        var result = await loader.LoadAsync();

        Assert.Single(result.Documents);
        Assert.Equal(expected, result.Documents[0].Content);
    }

    [Fact]
    public async Task LoadAsync_EmptyDocumentIsSkipped()
    {
        await WriteDocAsync("SOUL", "# Valid");
        await File.WriteAllTextAsync(Path.Combine(_tempWorkspace, "EMPTY.md"), "  ");

        var loader = CreateLoader();
        var result = await loader.LoadAsync();

        Assert.Single(result.Documents);
        Assert.Equal("SOUL", result.Documents[0].Name);
    }

    [Fact]
    public async Task LoadAsync_NonMdFilesAreSkipped()
    {
        await WriteDocAsync("SOUL", "# Soul");
        await File.WriteAllTextAsync(Path.Combine(_tempWorkspace, "notes.txt"), "text file");

        var loader = CreateLoader();
        var result = await loader.LoadAsync();

        Assert.Single(result.Documents);
        Assert.Contains("notes.txt", result.SkippedFiles);
    }

    [Fact]
    public async Task LoadAsync_ResultIsCached()
    {
        await WriteDocAsync("SOUL", "# Soul v1");

        var loader = CreateLoader();
        var result1 = await loader.LoadAsync();

        // Modify file after first load
        await WriteDocAsync("SOUL", "# Soul v2 (modified)");

        var result2 = await loader.LoadAsync();

        // Should be the same cached object
        Assert.Same(result1, result2);
        Assert.Equal("# Soul v1", result1.Documents[0].Content);
    }

    [Fact]
    public async Task ReloadAsync_ForcesNewReadFromDisk()
    {
        await WriteDocAsync("SOUL", "# Soul v1");
        var loader = CreateLoader();
        var result1 = await loader.LoadAsync();

        await WriteDocAsync("SOUL", "# Soul v2");
        var result2 = await loader.ReloadAsync();

        Assert.NotSame(result1, result2);
        Assert.Contains("v2", result2.Documents[0].Content);
    }

    [Fact]
    public async Task GetDocumentAsync_FindsByNameCaseInsensitive()
    {
        await WriteDocAsync("USER", "# User profile");
        var loader = CreateLoader();

        var doc = await loader.GetDocumentAsync("user");
        Assert.NotNull(doc);
        Assert.Equal("USER", doc!.Name);
    }

    [Fact]
    public async Task GetDocumentAsync_MissingDocReturnsNull()
    {
        var loader = CreateLoader();
        var doc = await loader.GetDocumentAsync("NONEXISTENT");
        Assert.Null(doc);
    }

    [Fact]
    public async Task BuildContextBlockAsync_EmptyWorkspace_ReturnsEmptyString()
    {
        var loader = CreateLoader();
        var block = await loader.BuildContextBlockAsync();
        Assert.Equal(string.Empty, block);
    }

    [Fact]
    public async Task BuildContextBlockAsync_ContainsAllDocumentNames()
    {
        await WriteDocAsync("SOUL", "I am DotnetClaw.");
        await WriteDocAsync("USER", "User is a developer.");

        var loader = CreateLoader();
        var block = await loader.BuildContextBlockAsync();

        Assert.Contains("SOUL", block);
        Assert.Contains("USER", block);
        Assert.Contains("I am DotnetClaw.", block);
        Assert.Contains("WORKSPACE IDENTITY DOCUMENTS", block);
    }

    [Fact]
    public async Task Summary_ReportsCorrectDocumentCount()
    {
        await WriteDocAsync("SOUL", "# Soul");
        await WriteDocAsync("USER", "# User");

        var loader = CreateLoader();
        var result = await loader.LoadAsync();

        Assert.Contains("2", result.Summary);
        Assert.Contains("SOUL", result.Summary);
        Assert.Contains("USER", result.Summary);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempWorkspace, recursive: true); } catch { /* best effort */ }
    }
}
