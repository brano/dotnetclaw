# DotnetClaw.Workflowy — Implementation Plan

## Naming

| Name | Role |
|---|---|
| **DotnetClaw.Workflowy** | The new project (CLI + SK plugin) |
| **Crabby** | Internal agent persona name for DotnetClaw (separate concern) |
| **Jobby** | Future background jobs project (not in scope here) |

## Context

DotnetClaw needs deterministic, resumable workflow execution. The Lobster CLI from OpenClaw solves this problem (typed pipelines, approval gates, durable state), but DotnetClaw needs a .NET-native equivalent that integrates as a Semantic Kernel plugin. **DotnetClaw.Workflowy** is that equivalent: a standalone C# CLI + SK plugin that runs YAML/JSON workflow files with sequential steps, environment/arg interpolation, and human approval gates backed by SQLite state.

---

## Project Structure

New project: `src/DotnetClaw.Workflowy/DotnetClaw.Workflowy.csproj` (`OutputType=Exe`, referenceable as library by `DotnetClaw`).

```
src/DotnetClaw.Workflowy/
├── DotnetClaw.Workflowy.csproj
├── Program.cs                    # CLI: workflowy run|resume|list
├── appsettings.json
├── Config/
│   └── WorkflowyOptions.cs
├── Models/
│   ├── WorkflowFile.cs           # parse-time: Workflow, WorkflowStep, ApprovalBlock
│   ├── WorkflowRun.cs            # EF entity (Id, Status, ResumeToken, ContextJson, NextStepIndex)
│   └── StepResult.cs             # EF entity (per-step stdout/stderr/exitCode)
├── Data/
│   └── WorkflowyDbContext.cs     # EF Core + SQLite
├── Engine/
│   ├── WorkflowLoader.cs         # YAML/JSON → WorkflowFile
│   ├── WorkflowEngine.cs         # orchestration, approval gate, resume
│   ├── StepExecutor.cs           # executes a single step (run:|pipeline:|approval:)
│   ├── PipelineDispatcher.cs     # handles pipeline: directives (llm.invoke stub, workflowy.run)
│   └── VariableResolver.cs       # {{stepname.stdout}}, {{args.x}}, {{env.X}}
└── Plugin/
    ├── WorkflowyPlugin.cs        # SK plugin: run_workflow, resume_workflow
    ├── WorkflowyRequest.cs       # inbound tool call JSON
    └── WorkflowyResponse.cs      # outbound JSON envelope
```

Test project: `tests/DotnetClaw.Workflowy.Tests/`

---

## Tool Call Protocol

**Run:**
```json
{"action": "run", "pipeline": "/path/to/workflow.yaml --arg1 value1", "timeoutMs": 30000}
```

**Resume:**
```json
{"action": "resume", "token": "<resumeToken>", "approve": true}
```

**Response envelope:**
```json
{
  "ok": true,
  "status": "ok | needs_approval | cancelled | error",
  "output": [{"summary": "..."}],
  "requiresApproval": {
    "type": "approval_request",
    "prompt": "Send 2 draft replies?",
    "items": [],
    "resumeToken": "..."
  }
}
```

---

## Workflow YAML Schema

```yaml
name: process-emails
args:
  - mailbox
env:
  SMTP_HOST: smtp.example.com
steps:
  - name: fetch_emails
    run: "python fetch.py --mailbox {{args.mailbox}}"

  - name: summarise
    pipeline: "llm.invoke --prompt 'Summarise: {{fetch_emails.stdout}}'"
    condition: "{{fetch_emails.exitCode}} == 0"

  - name: gate
    approval:
      prompt: "Send draft replies?"
      items: ["{{summarise.stdout}}"]

  - name: send
    run: "python send.py --data '{{summarise.stdout}}'"
```

Variable syntax: `{{stepname.stdout}}`, `{{stepname.stderr}}`, `{{stepname.exitCode}}`, `{{args.name}}`, `{{env.VAR}}`. Both `run:` and `command:` are valid synonyms for shell steps. JSON workflow files use the same field names.

---

## Key Design Decisions

| Decision | Choice | Reason |
|---|---|---|
| Project split | Single `OutputType=Exe` project | No abstraction tax; .NET supports referencing Exe as library |
| Resume token | URL-safe base64 GUID (22 chars) | Opaque, compact, globally unique |
| Variable syntax | `{{double-brace}}` | Avoids collision with shell's `$VAR` syntax |
| DbContext in DotnetClaw | `AddDbContextFactory<T>` (not `AddDbContext`) | DotnetClaw uses singleton lifetime throughout; factory pattern creates short-lived contexts per run |
| DB bootstrap | `EnsureCreated()` | Follows DotnetClawHub precedent; simple for personal-use tool |
| Output cap | 32 KB per step (configurable) | Prevent DB bloat from verbose commands |

---

## Data Models

**`WorkflowRun`** (EF entity):
- `Id`, `WorkflowName`, `WorkflowPath`, `Status` (Running/NeedsApproval/Completed/Cancelled/Failed)
- `ArgsJson` (serialized invocation args), `ContextJson` (accumulated interpolation state)
- `NextStepIndex` (resume pointer), `ResumeToken` (nullable, unique index)
- `StartedAt`, `CompletedAt`

**`StepResult`** (EF entity):
- `Id`, `WorkflowRunId`, `StepIndex`, `StepName`, `StepType` (run|pipeline|approval)
- `Status` (Success/Failed/Skipped/Approved/Rejected/TimedOut)
- `Stdout`, `Stderr` (capped), `ExitCode`, `WasTruncated`
- `StartedAt`, `CompletedAt`

No separate ApprovalToken table — resume token lives on `WorkflowRun.ResumeToken`.

---

## Engine Logic

**`WorkflowEngine.RunAsync(path, args, timeoutMs)`:**
1. Load + validate `WorkflowFile`
2. Persist new `WorkflowRun{Status=Running}`
3. Build initial context: `args.*`, `env.*`
4. For each step from `NextStepIndex`:
   - Evaluate `condition` → skip if false, record `StepResult{Skipped}`
   - If `approval:` block → generate token, persist `run.ResumeToken`, set `Status=NeedsApproval`, snapshot `ContextJson`, save, **return** `{status: "needs_approval"}`
   - Otherwise `StepExecutor.ExecuteAsync()` → persist `StepResult`, add `stepname.*` to context
   - On step failure → set `Status=Failed`, return error envelope
5. Complete: set `Status=Completed`, return `{status: "ok"}`

**`WorkflowEngine.ResumeAsync(token, approved)`:**
1. Lookup run by `ResumeToken` — error if not found or not `NeedsApproval`
2. If `!approved` → set `Status=Cancelled`, clear token, return `{status: "cancelled"}`
3. If `approved` → clear token, restore context from `ContextJson`, advance `NextStepIndex` past approval step, set `Status=Running`, continue step loop

---

## `WorkflowyPlugin.cs` (SK Integration)

```csharp
[KernelFunction("run_workflow")]
[Description("Run a Workflowy workflow file. Pass JSON: {action:'run', pipeline:'/path/to.yaml --arg val', timeoutMs:30000}")]
public async Task<string> RunWorkflowAsync(string requestJson, CancellationToken ct)

[KernelFunction("resume_workflow")]
[Description("Resume a workflow waiting for approval. Pass JSON: {action:'resume', token:'<token>', approve:true}")]
public async Task<string> ResumeWorkflowAsync(string requestJson, CancellationToken ct)
```

Both return serialized `WorkflowyResponse` JSON. Single `string requestJson` parameter per function (matches the tool call protocol directly).

---

## Files to Modify

### `DotnetClaw.sln`
- Add `DotnetClaw.Workflowy` project entry (GUID: `{F1E2D3C4-B5A6-7890-FEDC-BA9876543210}`)
- Add `DotnetClaw.Workflowy.Tests` project entry (GUID: `{E2D3C4B5-A6F7-8901-EDCB-A98765432101}`)
- Nest both under `src` (`{C3D4E5F6-...}`) and `tests` (`{D4E5F6A7-...}`) solution folders

### `Directory.Packages.props`
- Add `<PackageVersion Include="YamlDotNet" Version="16.3.0" />` under a new `Workflow Parsing` group

### `src/DotnetClaw/DotnetClaw.csproj`
- Add `<ProjectReference Include="..\DotnetClaw.Workflowy\DotnetClaw.Workflowy.csproj" />`

### `src/DotnetClaw/Agents/KernelFactory.cs` (line 71, after McpPlugin)
```csharp
builder.Plugins.AddFromObject(
    services.GetRequiredService<WorkflowyPlugin>(), "Workflowy");
```

### `src/DotnetClaw/Program.cs`
Add in DI registration block (after existing plugins):
```csharp
builder.Services
    .Configure<WorkflowyOptions>(builder.Configuration.GetSection($"{DotnetClawOptions.SectionName}:Workflowy"))
    .AddDbContextFactory<WorkflowyDbContext>((sp, opts) => {
        var o = sp.GetRequiredService<IOptions<WorkflowyOptions>>().Value;
        opts.UseSqlite($"Data Source={o.ResolvedDatabasePath}");
    })
    .AddSingleton<WorkflowLoader>()
    .AddSingleton<VariableResolver>()
    .AddSingleton<StepExecutor>()
    .AddSingleton<PipelineDispatcher>()
    .AddSingleton<WorkflowEngine>()
    .AddSingleton<WorkflowyPlugin>();
```

### `src/DotnetClaw/appsettings.json`
Add under `"DotnetClaw"`:
```json
"Workflowy": {
  "DatabasePath": "~/.workflowy/workflowy.db",
  "StepOutputCaptureLimitBytes": 32768,
  "DefaultStepTimeoutSeconds": 60,
  "MaxStepTimeoutSeconds": 600
}
```

---

## `WorkflowyOptions.cs`

```csharp
public sealed class WorkflowyOptions {
    public const string SectionName = "Workflowy";
    public string DatabasePath { get; set; } = "~/.workflowy/workflowy.db";
    public int StepOutputCaptureLimitBytes { get; set; } = 32_768;
    public int DefaultStepTimeoutSeconds { get; set; } = 60;
    public int MaxStepTimeoutSeconds { get; set; } = 600;
    public string? ShellExecutable { get; set; }   // default: cmd.exe /c or sh -c
    public string? ShellArgPrefix { get; set; }
    public string ResolvedDatabasePath => /* expands ~ to home dir */
}
```

---

## StepExecutor Platform Notes

- `run:` steps: wrap command in `cmd.exe /c` (Windows) or `sh -c` (Unix) — detected via `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)`, overridable via `WorkflowyOptions.ShellExecutable`
- Reuse process launch pattern from `src/DotnetClaw/Plugins/ShellPlugin.cs`
- Capture stdout/stderr to `StringBuilder` with byte cap; truncate with marker if limit exceeded
- Timeout via `CancellationTokenSource` linked to per-step deadline

---

---

## Multi-Surface Approval Architecture

Approvals must work across three surfaces: **CLI console**, **Web Terminal**, and **Web Chat UI**. Additionally the Web UI gets a dedicated **Human Tasks section** for managing pending approvals independently of chat.

### Approval Flow Overview

```
WorkflowEngine hits approval: gate
    │
    ├─→ Persists WorkflowRun{Status=NeedsApproval, ResumeToken=<token>}
    ├─→ Returns WorkflowyResponse{status:"needs_approval", requiresApproval:{...token...}}
    │
    └─→ WorkflowyPlugin receives response
            │
            ├─→ Fires IApprovalNotifier.NotifyPendingAsync(pendingApproval)
            │       │
            │       └─→ GatewayHub broadcasts "workflowy_pending" to web clients
            │               │
            │               └─→ WebGatewayClientService dispatches to WorkflowyApprovalService
            │                       └─→ fires OnTasksChanged → /tasks page updates
            │
            └─→ Returns JSON envelope to SK agent
                    │
                    ├─→ Chat UI: agent explains approval needed, user replies "approve"
                    │   → agent calls resume_workflow(token, approve=true)
                    │
                    ├─→ Terminal: agent prints approval prompt, user types "approve"
                    │   → TerminalService routes to agent → agent calls resume_workflow
                    │
                    └─→ CLI REPL: agent prints approval prompt, user types at prompt
                        → agent calls resume_workflow
```

When user approves via **Human Tasks Web UI**:
```
User clicks Approve on /tasks page
    → WorkflowyApprovalService.ApproveAsync(token, true)
    → WebGatewayClientService.SendAsync("ApproveWorkflow", token, true)
    → GatewayHub.ApproveWorkflow(token, approved)
    → WorkflowEngine.ResumeAsync(token, approved)
    → Broadcasts "workflowy_resumed" message back to web clients
    → WorkflowyApprovalService removes task from list, fires OnTasksChanged
```

### New Gateway Message Types

In `src/DotnetClaw/Gateway/GatewayMessage.cs`, add to `MessageType`:
```csharp
public const string WorkflowyPending = "workflowy_pending";   // server → all web clients
public const string WorkflowyResumed = "workflowy_resumed";   // server → all web clients
```

In `src/DotnetClaw/Gateway/GatewayHub.cs`, add new Hub method:
```csharp
public async Task ApproveWorkflow(string token, bool approved)
// Calls WorkflowEngine.ResumeAsync, broadcasts WorkflowyResumed
```

In `src/DotnetClaw/Gateway/IGatewayClient.cs`, add:
```csharp
Task ReceiveWorkflowyPending(PendingApprovalDto approval);
Task ReceiveWorkflowyResumed(string token, string status);
```

### New DotnetClaw.Web Components

**`WorkflowyApprovalService`** (singleton, `src/DotnetClaw.Web/Services/`):
- Maintains `List<PendingApprovalDto>` — all open approvals
- Subscribes to `workflowy_pending` / `workflowy_resumed` via `WebGatewayClientService` using a fixed session ID (`"workflowy-tasks"`)
- Exposes `IReadOnlyList<PendingApprovalDto> PendingTasks { get; }`
- Exposes `event Action? OnTasksChanged`
- `Task ApproveAsync(string token, bool approved)` — sends `ApproveWorkflow` via gateway

**`PendingApprovalDto`** (shared DTO, lives in `DotnetClaw.Workflowy/Protocol/`):
```csharp
public sealed record PendingApprovalDto(
    string Token,
    string WorkflowName,
    string Prompt,
    IReadOnlyList<string> Items,
    DateTimeOffset RequestedAt);
```

**`/tasks` Blazor page** (`src/DotnetClaw.Web/Components/Pages/Tasks.razor`):
- Navigation entry in `MainLayout.razor` (sidebar icon: checklist)
- Shows count badge when `PendingTasks.Count > 0`
- Lists each pending approval with: workflow name, prompt, items (expandable), timestamp
- Approve / Reject buttons per task
- Subscribes to `WorkflowyApprovalService.OnTasksChanged`

### CLI/Terminal Approval Surface

The SK agent automatically handles CLI and terminal surfaces because `WorkflowyPlugin.RunWorkflowAsync` returns the `needs_approval` JSON envelope as a string back to the agent. The agent reads `status:"needs_approval"` and `requiresApproval.prompt` and presents it conversationally:

- In **chat**: the agent message reads _"Workflow paused — approval needed: [prompt]. Reply 'approve' or 'reject'."_
- In **terminal**: same text flows through `TerminalService.OnOutput`
- In **CLI REPL**: same text appears in the console

The agent then listens for the user's next message and calls `resume_workflow` with the token accordingly. No special REPL handling needed.

---

## Implementation Sequence

1. **Foundation** — project file, solution entries, CPM entry, `WorkflowyOptions`, `WorkflowyDbContext`, model classes
2. **Parser** — `WorkflowLoader` (YAML+JSON), `VariableResolver`, loader tests
3. **Execution** — `StepExecutor` (run steps), `PipelineDispatcher` (llm.invoke stub)
4. **Engine** — `WorkflowEngine.RunAsync`, approval gate, `ResumeAsync`, engine tests
5. **CLI** — `Program.cs` with `run`/`resume`/`list` subcommands, exit codes (0=ok, 1=failed, 2=needs_approval, 3=usage error)
6. **DotnetClaw integration** — `WorkflowyPlugin`, `WorkflowyRequest/Response`, add to `KernelFactory`, register in `DotnetClaw/Program.cs`, plugin tests
7. **Gateway approval messages** — new `MessageType` constants, `GatewayHub.ApproveWorkflow`, `IGatewayClient` additions
8. **Web Human Tasks** — `WorkflowyApprovalService`, `Tasks.razor` page, sidebar nav entry with badge

---

## Verification

1. **Build**: `dotnet build DotnetClaw.sln` — 0 errors including new projects
2. **Unit tests**: `dotnet test tests/DotnetClaw.Workflowy.Tests/` — VariableResolver, WorkflowLoader, engine approval flow
3. **Standalone CLI**: `dotnet run --project src/DotnetClaw.Workflowy -- run tests/sample.yaml`
4. **DotnetClaw integration**: run DotnetClaw CLI, ask agent to run a workflow file → verify `run_workflow` tool returns JSON envelope
5. **Agent-mediated approval (chat/terminal)**: workflow with `approval:` step → agent presents prompt → user types "approve" → agent calls `resume_workflow` → completes
6. **Web Human Tasks approval**: workflow with `approval:` step → `/tasks` page shows pending task with badge → user clicks Approve → task disappears, workflow resumes
7. **SQLite persistence**: `~/.workflowy/workflowy.db` contains `WorkflowRuns` and `StepResults` after a run
