using DotnetClaw.Workflowy.Engine;
using Xunit;

namespace DotnetClaw.Workflowy.Tests;

public sealed class WorkflowLoaderTests
{
    private readonly WorkflowLoader _sut = new();

    private const string ValidYaml = """
        name: greet
        args:
          - name
        env:
          GREETING: hello
        steps:
          - name: say_hi
            run: "echo {{args.name}}"
          - approval:
              prompt: "Confirm?"
              items: []
          - name: done
            run: "echo done"
        """;

    private const string ValidJson = """
        {
          "name": "greet",
          "args": ["name"],
          "env": { "GREETING": "hello" },
          "steps": [
            { "name": "say_hi", "run": "echo {{args.name}}" },
            { "approval": { "prompt": "Confirm?", "items": [] } },
            { "name": "done", "run": "echo done" }
          ]
        }
        """;

    [Fact]
    public void LoadFromString_ParsesYamlCorrectly()
    {
        var wf = _sut.LoadFromString(ValidYaml, "yaml");

        Assert.Equal("greet", wf.Name);
        Assert.Single(wf.Args);
        Assert.Equal("name", wf.Args[0]);
        Assert.Equal("hello", wf.Env["GREETING"]);
        Assert.Equal(3, wf.Steps.Count);
        Assert.Equal("say_hi", wf.Steps[0].Name);
        Assert.NotNull(wf.Steps[1].Approval);
        Assert.Equal("Confirm?", wf.Steps[1].Approval!.Prompt);
    }

    [Fact]
    public void LoadFromString_ParsesJsonCorrectly()
    {
        var wf = _sut.LoadFromString(ValidJson, "json");

        Assert.Equal("greet", wf.Name);
        Assert.Equal(3, wf.Steps.Count);
        Assert.NotNull(wf.Steps[1].Approval);
    }

    [Fact]
    public void LoadFromString_CommandSynonymWorksInYaml()
    {
        var yaml = """
            name: test
            steps:
              - name: run_it
                command: "echo hi"
            """;
        var wf = _sut.LoadFromString(yaml);
        Assert.Equal("echo hi", wf.Steps[0].EffectiveRun);
    }

    [Fact]
    public void Validate_ReturnsNoErrorsForValidWorkflow()
    {
        var wf = _sut.LoadFromString(ValidYaml);
        var errors = _sut.Validate(wf);
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ErrorsWhenNameMissing()
    {
        var yaml = "steps:\n  - run: echo hi\n";
        var wf = _sut.LoadFromString(yaml);
        var errors = _sut.Validate(wf);
        Assert.Contains(errors, e => e.Contains("name"));
    }

    [Fact]
    public void Validate_ErrorsOnDuplicateStepName()
    {
        var yaml = """
            name: test
            steps:
              - name: a
                run: echo 1
              - name: a
                run: echo 2
            """;
        var wf = _sut.LoadFromString(yaml);
        var errors = _sut.Validate(wf);
        Assert.Contains(errors, e => e.Contains("Duplicate") && e.Contains("a"));
    }

    [Fact]
    public void Validate_ErrorsWhenStepHasNoAction()
    {
        var yaml = """
            name: test
            steps:
              - name: empty_step
            """;
        var wf = _sut.LoadFromString(yaml);
        var errors = _sut.Validate(wf);
        Assert.Contains(errors, e => e.Contains("no action"));
    }
}
