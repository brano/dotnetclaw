using DotnetClaw.Workflowy.Config;
using DotnetClaw.Workflowy.Data;
using DotnetClaw.Workflowy.Engine;
using DotnetClaw.Workflowy.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Text.Json;

// ============================================================================
//  DotnetClaw.Workflowy — Deterministic Workflow Shell CLI
//
//  Usage:
//    workflowy run <workflow.yaml> [--key value ...]
//    workflowy resume <token> [--approve | --reject]
//    workflowy list [--last N]
// ============================================================================

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    PrintHelp();
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLogging(l =>
{
    l.AddConsole();
    l.AddConfiguration(builder.Configuration.GetSection("Logging"));
});

builder.Services
    .Configure<WorkflowyOptions>(builder.Configuration.GetSection(WorkflowyOptions.SectionName))
    .AddDbContextFactory<WorkflowyDbContext>((sp, opts) =>
    {
        var o = sp.GetRequiredService<IOptions<WorkflowyOptions>>().Value;
        opts.UseSqlite($"Data Source={o.ResolvedDatabasePath}");
    })
    .AddSingleton<WorkflowLoader>()
    .AddSingleton<VariableResolver>()
    .AddSingleton<StepExecutor>()
    .AddSingleton<PipelineDispatcher>()
    .AddSingleton<WorkflowEngine>();

var app = builder.Build();

// Ensure DB directory and schema exist
var wOpts = app.Services.GetRequiredService<IOptions<WorkflowyOptions>>().Value;
Directory.CreateDirectory(Path.GetDirectoryName(wOpts.ResolvedDatabasePath)!);
var dbFactory = app.Services.GetRequiredService<IDbContextFactory<WorkflowyDbContext>>();
await using (var db = await dbFactory.CreateDbContextAsync())
    await db.Database.EnsureCreatedAsync();

var engine = app.Services.GetRequiredService<WorkflowEngine>();
var subcommand = args[0].ToLowerInvariant();

return subcommand switch
{
    "run"    => await HandleRunAsync(args[1..], engine),
    "resume" => await HandleResumeAsync(args[1..], engine),
    "list"   => await HandleListAsync(args[1..], dbFactory),
    _ => UnknownCommand(args[0]),
};

// ─────────────────────────────────────────────────────────────────────────────

static async Task<int> HandleRunAsync(string[] args, WorkflowEngine engine)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("Usage: workflowy run <workflow.yaml> [--key value ...]");
        return 3;
    }

    var path = args[0];
    var runArgs = ParseKeyValueArgs(args[1..]);
    var response = await engine.RunAsync(path, runArgs, 30_000, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true }));

    return response.Status switch
    {
        "ok"            => 0,
        "needs_approval" => 2,
        _               => 1,
    };
}

static async Task<int> HandleResumeAsync(string[] args, WorkflowEngine engine)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("Usage: workflowy resume <token> [--approve | --reject]");
        return 3;
    }

    var token = args[0];
    var approved = !args.Contains("--reject");
    var response = await engine.ResumeAsync(token, approved, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true }));
    return response.Status == "ok" ? 0 : 1;
}

static async Task<int> HandleListAsync(string[] args, IDbContextFactory<WorkflowyDbContext> dbFactory)
{
    var lastN = 10;
    for (var i = 0; i + 1 < args.Length; i++)
    {
        if (args[i] == "--last" && int.TryParse(args[i + 1], out var n))
            lastN = n;
    }

    await using var db = await dbFactory.CreateDbContextAsync();
    var runs = await db.WorkflowRuns
        .OrderByDescending(r => r.StartedAt)
        .Take(lastN)
        .ToListAsync();

    if (runs.Count == 0)
    {
        Console.WriteLine("No workflow runs found.");
        return 0;
    }

    Console.WriteLine($"{"ID",-6} {"Workflow",-30} {"Status",-16} {"Started"}");
    Console.WriteLine(new string('-', 72));
    foreach (var run in runs)
    {
        Console.WriteLine(
            $"{run.Id,-6} {run.WorkflowName,-30} {run.Status,-16} {run.StartedAt:yyyy-MM-dd HH:mm:ss}");
        if (run.Status == WorkflowRunStatus.NeedsApproval && run.ResumeToken is not null)
            Console.WriteLine($"{"",6}  Token: {run.ResumeToken}");
    }
    return 0;
}

static int UnknownCommand(string cmd)
{
    Console.Error.WriteLine($"Unknown command: '{cmd}'");
    PrintHelp();
    return 3;
}

static Dictionary<string, string> ParseKeyValueArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i + 1 < args.Length; i += 2)
        result[args[i].TrimStart('-')] = args[i + 1];
    return result;
}

static void PrintHelp()
{
    Console.WriteLine("""
        DotnetClaw.Workflowy — Deterministic Workflow Shell

        Usage:
          workflowy run <workflow.yaml> [--key value ...]
          workflowy resume <token> [--approve | --reject]
          workflowy list [--last N]

        Commands:
          run     Execute a YAML or JSON workflow file
          resume  Resume a paused workflow (approve or reject an approval gate)
          list    Show recent workflow runs (default: last 10)

        Exit codes:
          0  Success
          1  Failed / cancelled / rejected
          2  Paused (needs_approval — token is in the JSON output)
          3  Usage / parse error

        Example workflow (sample.yaml):
          name: greet
          args:
            - name
          steps:
            - name: say_hello
              run: "echo Hello, {{args.name}}!"

            - approval:
                prompt: "Confirm greeting?"
                items: ["{{say_hello.stdout}}"]

            - name: confirm
              run: "echo Greeting confirmed."
        """);
}
