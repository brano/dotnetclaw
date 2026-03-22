using DotnetClaw.UI;
using Xunit;

namespace DotnetClaw.Tests;

public sealed class MarkdownAnsiStreamFormatterTests
{
    private const string Bold = "\x1b[1m";
    private const string Reset = "\x1b[0m";

    [Fact]
    public void Append_CompleteBoldOnOneChunk_AppliesBold()
    {
        var f = new MarkdownAnsiStreamFormatter();
        var s = f.Append("Say **hello** there.") + f.Flush();
        Assert.Contains($"{Bold}hello{Reset}", s);
        Assert.DoesNotContain("**", s);
    }

    [Fact]
    public void Append_BoldSplitAcrossChunks_StillRendersBold()
    {
        var f = new MarkdownAnsiStreamFormatter();
        var a = f.Append("Say **hel");
        Assert.DoesNotContain(Bold, a);
        var b = f.Append("lo** end.");
        Assert.Contains($"{Bold}hello{Reset}", a + b);
        var c = f.Flush();
        Assert.DoesNotContain("**", a + b + c);
    }

    [Fact]
    public void Append_HeaderLine_UsesHeaderStyle()
    {
        var f = new MarkdownAnsiStreamFormatter();
        var s = f.Append("## Section\r\n") + f.Flush();
        Assert.Contains("\x1b[1;96m", s);
        Assert.Contains("Section", s);
        Assert.DoesNotContain("##", s);
    }

    [Fact]
    public void Append_UnorderedList_UsesBullet()
    {
        var f = new MarkdownAnsiStreamFormatter();
        var s = f.Append("- first\n- second\n") + f.Flush();
        Assert.Contains("•", s);
        Assert.Contains("first", s);
        Assert.Contains("second", s);
    }

    [Fact]
    public void Append_OrderedList_PreservesNumber()
    {
        var f = new MarkdownAnsiStreamFormatter();
        var s = f.Append("1. one\n2. two\n") + f.Flush();
        Assert.Contains("1.", s);
        Assert.Contains("2.", s);
    }

    [Fact]
    public void Reset_ClearsPendingHoldBack()
    {
        var f = new MarkdownAnsiStreamFormatter();
        _ = f.Append("**in");
        f.Reset();
        var s = f.Append("**bold**") + f.Flush();
        Assert.Contains($"{Bold}bold{Reset}", s);
    }
}
