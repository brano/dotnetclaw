using DotnetClaw.Telegram;
using Xunit;

namespace DotnetClaw.Tests;

public class TelegramCommandRouterTests
{
    // ── Parse — free text ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_PlainText_ReturnsFreeText()
    {
        var cmd = TelegramCommandRouter.Parse("What is dependency injection?");
        Assert.Equal(TelegramCommand.FreeText, cmd.Command);
        Assert.Equal("What is dependency injection?", cmd.Argument);
    }

    [Fact]
    public void Parse_EmptyText_ReturnsFreeText()
    {
        var cmd = TelegramCommandRouter.Parse("");
        Assert.Equal(TelegramCommand.FreeText, cmd.Command);
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsFreeText()
    {
        var cmd = TelegramCommandRouter.Parse("   ");
        Assert.Equal(TelegramCommand.FreeText, cmd.Command);
    }

    // ── Parse — /ask ──────────────────────────────────────────────────────────

    [Fact]
    public void Parse_AskCommand_ReturnsAsk()
    {
        var cmd = TelegramCommandRouter.Parse("/ask How does ClawAgentLoop work?");
        Assert.Equal(TelegramCommand.Ask, cmd.Command);
        Assert.Equal("How does ClawAgentLoop work?", cmd.Argument);
    }

    [Fact]
    public void Parse_AskWithoutArgument_ReturnsMissingArgs()
    {
        var cmd = TelegramCommandRouter.Parse("/ask");
        Assert.Equal(TelegramCommand.MissingArgs, cmd.Command);
    }

    // ── Parse — /plan ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/plan Add JWT auth to the API")]
    [InlineData("/cursor_plan Add JWT auth to the API")]
    public void Parse_PlanCommands_ReturnCursorPlan(string text)
    {
        var cmd = TelegramCommandRouter.Parse(text);
        Assert.Equal(TelegramCommand.CursorPlan, cmd.Command);
        Assert.Equal("Add JWT auth to the API", cmd.Argument);
    }

    [Fact]
    public void Parse_PlanWithoutArgs_ReturnsMissingArgs()
    {
        var cmd = TelegramCommandRouter.Parse("/plan");
        Assert.Equal(TelegramCommand.MissingArgs, cmd.Command);
    }

    // ── Parse — /agent ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/agent Refactor AuthService")]
    [InlineData("/cursor_agent Refactor AuthService")]
    public void Parse_AgentCommands_ReturnCursorAgent(string text)
    {
        var cmd = TelegramCommandRouter.Parse(text);
        Assert.Equal(TelegramCommand.CursorAgent, cmd.Command);
        Assert.Equal("Refactor AuthService", cmd.Argument);
    }

    // ── Parse — /cursor_ask ───────────────────────────────────────────────────

    [Fact]
    public void Parse_CursorAsk_ReturnsCursorAsk()
    {
        var cmd = TelegramCommandRouter.Parse("/cursor_ask What does the retry logic do?");
        Assert.Equal(TelegramCommand.CursorAsk, cmd.Command);
        Assert.Equal("What does the retry logic do?", cmd.Argument);
    }

    // ── Parse — meta commands ─────────────────────────────────────────────────

    [Fact]
    public void Parse_Reset_ReturnsReset()
    {
        var cmd = TelegramCommandRouter.Parse("/reset");
        Assert.Equal(TelegramCommand.Reset, cmd.Command);
    }

    [Fact]
    public void Parse_Status_ReturnsStatus()
    {
        var cmd = TelegramCommandRouter.Parse("/status");
        Assert.Equal(TelegramCommand.Status, cmd.Command);
    }

    [Theory]
    [InlineData("/help")]
    [InlineData("/start")]
    public void Parse_HelpAndStart_ReturnHelp(string text)
    {
        var cmd = TelegramCommandRouter.Parse(text);
        Assert.Equal(TelegramCommand.Help, cmd.Command);
    }

    [Fact]
    public void Parse_UnknownCommand_ReturnsUnknown()
    {
        var cmd = TelegramCommandRouter.Parse("/doesthingnotexist");
        Assert.Equal(TelegramCommand.Unknown, cmd.Command);
    }

    // ── Parse — @BotName suffix stripping ────────────────────────────────────

    [Fact]
    public void Parse_CommandWithBotNameSuffix_Strips()
    {
        var cmd = TelegramCommandRouter.Parse("/ask@DotnetClawBot How are you?");
        Assert.Equal(TelegramCommand.Ask, cmd.Command);
        Assert.Equal("How are you?", cmd.Argument);
    }

    [Fact]
    public void Parse_PlanWithBotNameSuffix_Strips()
    {
        var cmd = TelegramCommandRouter.Parse("/plan@DotnetClawBot Implement caching");
        Assert.Equal(TelegramCommand.CursorPlan, cmd.Command);
        Assert.Equal("Implement caching", cmd.Argument);
    }

    // ── Parse — case insensitivity ────────────────────────────────────────────

    [Theory]
    [InlineData("/ASK question")]
    [InlineData("/Ask question")]
    [InlineData("/ask question")]
    public void Parse_CommandCaseInsensitive(string text)
    {
        var cmd = TelegramCommandRouter.Parse(text);
        Assert.Equal(TelegramCommand.Ask, cmd.Command);
    }

    // ── EscapeMarkdown ────────────────────────────────────────────────────────

    [Fact]
    public void EscapeMarkdown_EscapesSpecialChars()
    {
        var escaped = TelegramCommandRouter.EscapeMarkdown("Hello! Build_SUCCESS. (1+2)=3");
        Assert.DoesNotContain("!", escaped.Replace("\\!", ""));  // ! is escaped
        Assert.Contains("\\!", escaped);
        Assert.Contains("\\_", escaped);
        Assert.Contains("\\.", escaped);
        Assert.Contains("\\(", escaped);
        Assert.Contains("\\)", escaped);
        Assert.Contains("\\+", escaped);
        Assert.Contains("\\=", escaped);
    }

    [Fact]
    public void EscapeMarkdown_PlainText_NoChangeExceptEscaping()
    {
        var input = "Hello World";
        var escaped = TelegramCommandRouter.EscapeMarkdown(input);
        // Plain letters and spaces don't need escaping
        Assert.Equal("Hello World", escaped);
    }

    // ── Status / Help responses ───────────────────────────────────────────────

    [Fact]
    public void Parse_Status_ArgumentIsEmpty()
    {
        var cmd = TelegramCommandRouter.Parse("/status");
        Assert.Equal(string.Empty, cmd.Argument);
    }

    [Fact]
    public void Parse_ResetWithExtraText_ArgumentIsIgnored()
    {
        // /reset doesn't care about arguments
        var cmd = TelegramCommandRouter.Parse("/reset please");
        Assert.Equal(TelegramCommand.Reset, cmd.Command);
    }
}
