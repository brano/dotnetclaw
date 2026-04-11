using DotnetClaw.Workflowy.Engine;
using DotnetClaw.Workflowy.Models;

namespace DotnetClaw.Workflowy.Tests;

public sealed class VariableResolverTests
{
    private readonly VariableResolver _sut = new();

    [Fact]
    public void Resolve_SubstitutesKnownToken()
    {
        var ctx = new Dictionary<string, string> { ["args.name"] = "world" };
        var result = _sut.Resolve("Hello, {{args.name}}!", ctx);
        Assert.Equal("Hello, world!", result);
    }

    [Fact]
    public void Resolve_LeavesUnknownTokenUnchanged()
    {
        var ctx = new Dictionary<string, string>();
        var result = _sut.Resolve("Hello, {{args.name}}!", ctx);
        Assert.Equal("Hello, {{args.name}}!", result);
    }

    [Fact]
    public void Resolve_MultipleTokensInOneTemplate()
    {
        var ctx = new Dictionary<string, string>
        {
            ["args.greeting"] = "Hi",
            ["args.name"] = "Alice",
        };
        var result = _sut.Resolve("{{args.greeting}}, {{args.name}}!", ctx);
        Assert.Equal("Hi, Alice!", result);
    }

    [Fact]
    public void BuildInitialContext_MapsEnvAndArgs()
    {
        var wf = new WorkflowFile
        {
            Name = "test",
            Env = new() { ["API_URL"] = "http://localhost" },
            Args = ["limit"],
        };
        var ctx = _sut.BuildInitialContext(wf, new Dictionary<string, string> { ["limit"] = "20" });

        Assert.Equal("http://localhost", ctx["env.API_URL"]);
        Assert.Equal("20", ctx["args.limit"]);
    }

    [Fact]
    public void AddStepOutputs_PopulatesCorrectKeys()
    {
        var ctx = new Dictionary<string, string>();
        var result = new StepResult
        {
            Stdout = "hello",
            Stderr = "warn",
            ExitCode = 0,
        };
        _sut.AddStepOutputs(ctx, "fetch", result);

        Assert.Equal("hello", ctx["fetch.stdout"]);
        Assert.Equal("warn", ctx["fetch.stderr"]);
        Assert.Equal("0", ctx["fetch.exitCode"]);
        Assert.Equal("true", ctx["fetch.success"]);
    }

    [Theory]
    [InlineData("{{x}} == 0", "x", "0", true)]
    [InlineData("{{x}} == 0", "x", "1", false)]
    [InlineData("{{x}} != 0", "x", "1", true)]
    [InlineData("true", null, null, true)]
    [InlineData("false", null, null, false)]
    [InlineData("1", null, null, true)]
    [InlineData("0", null, null, false)]
    public void EvaluateCondition_ReturnsExpected(string condition, string? key, string? value, bool expected)
    {
        var ctx = new Dictionary<string, string>();
        if (key is not null && value is not null) ctx[key] = value;
        Assert.Equal(expected, _sut.EvaluateCondition(condition, ctx));
    }
}
