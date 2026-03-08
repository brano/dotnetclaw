# 🦀 DotnetClaw

> 🦞 OpenClaw-inspired Personal AI Assistant built on **.NET 10** and **Microsoft Semantic Kernel**'s Agent Framework.

---

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- An OpenAI API key (or Azure OpenAI / Anthropic — see providers)

### Run

```bash
# Set your API key
set OPENAI_API_KEY=sk-...

# Restore & run
cd src/DotnetClaw
dotnet run
```

### Configuration

| Environment Variable        | Description                          | Default  |
|-----------------------------|--------------------------------------|----------|
| `DOTNETCLAW_PROVIDER`       | `openai` \| `azure` \| `anthropic`   | `openai` |
| `OPENAI_API_KEY`            | OpenAI API key                       | —        |
| `AZURE_OPENAI_ENDPOINT`     | Azure OpenAI endpoint URL            | —        |
| `AZURE_OPENAI_API_KEY`      | Azure OpenAI key                     | —        |
| `AZURE_OPENAI_DEPLOYMENT`   | Azure deployment name                | Model ID |

Or edit `appsettings.json` directly.

### REPL Commands

| Command   | Action                          |
|-----------|---------------------------------|
| `help`    | Show available commands         |
| `reset`   | Clear conversation history      |
| `history` | Print full conversation history |
| `exit`    | Quit                            |

### Run Tests

```bash
cd tests/DotnetClaw.Tests
dotnet test
```

## Agentic Loop

```
User Input
    │
    ▼
ClawAgentLoop.RunTurnAsync()
    │
    ├──► ChatCompletionAgent (SK)
    │        │
    │        ├── FunctionChoiceBehavior.Auto()  ← auto tool-call selection
    │        │
    │        ├── [Tool Call] Shell.run_command
    │        ├── [Tool Call] FileSystem.read_file
    │        ├── [Tool Call] Dotnet.find_csharp_projects
    │        │        ▲
    │        │        └── results fed back into context
    │        │
    │        └── Final text response  ◄── streamed to terminal
    │
    └──► Max iterations guard (default: 20)
```

## Workspace Identity Documents

On every session start (and on `reset`), DotnetClaw loads `*.md` files from `./workspace/`
and injects them into the system prompt before any user message is sent.

```
./workspace/
├── SOUL.md      ← Who the agent is (personality, values, style)
├── AGENTS.md    ← How it uses tools and handles multi-agent flows
├── USER.md      ← Who you are (role, prefs, tech stack)
├── CONTEXT.md   ← What you're working on right now
├── MEMORY.md    ← Persistent facts you want remembered
└── *.md         ← Any additional documents, loaded alphabetically
```

**Loading order** is controlled by `WorkspaceDocumentPriority` in `appsettings.json`
(default: `SOUL → AGENTS → USER → CONTEXT → MEMORY → TOOLS → RULES`).
Remaining `*.md` files follow alphabetically.

The workspace folder is **optional** — if it doesn't exist, DotnetClaw starts with the
base system prompt only.

### REPL commands

| Command          | Action                                          |
|------------------|-------------------------------------------------|
| `workspace`      | Show loaded documents table                     |
| `ws reload`      | Force reload from disk without resetting chat   |
| `prompt`         | Print the full effective system prompt          |

### Runtime skill

The agent can query workspace docs mid-conversation via the `Workspace` plugin:
- `list_workspace_docs` — table of loaded docs
- `get_workspace_doc SOUL` — fetch a specific doc's content
- `reload_workspace` — hot-reload from disk
- `get_workspace_context` — full injected context block

---

## Skills (Plugins)

### Shell
| Function              | Description                            |
|-----------------------|----------------------------------------|
| `run_command`         | Execute any shell command              |
| `list_directory`      | Tree-style directory listing           |
| `get_working_directory` | Return current working directory     |

### FileSystem
| Function       | Description                     |
|----------------|---------------------------------|
| `read_file`    | Read a text file                |
| `write_file`   | Create or overwrite a file      |
| `append_file`  | Append to an existing file      |
| `delete_file`  | Delete a file                   |
| `file_exists`  | Check if a path exists          |
| `find_files`   | Glob search for files           |

### Dotnet
| Function                  | Description                          |
|---------------------------|--------------------------------------|
| `find_csharp_projects`    | Locate .csproj files                 |
| `summarise_csharp_file`   | Structural summary of a .cs file     |
| `get_nuget_packages`      | List NuGet packages in a .csproj     |

### Workspace
| Function                  | Description                                        |
|---------------------------|----------------------------------------------------|
| `list_workspace_docs`     | Table of all loaded identity documents             |
| `get_workspace_doc`       | Fetch full content of a specific document          |
| `reload_workspace`        | Force reload all docs from disk                    |
| `get_workspace_context`   | Full combined context block (as injected)          |

### Cursor CLI (agent)
| Function       | Mode    | File changes? | Description                                   |
|----------------|---------|---------------|-----------------------------------------------|
| `cursor_agent` | `agent` | ✅ Yes        | Autonomous coding — reads, plans, edits files |
| `cursor_plan`  | `plan`  | ❌ No         | Returns a step-by-step plan, no edits         |
| `cursor_ask`   | `ask`   | ❌ No         | Q&A about the codebase, read-only             |
| `cursor_run`   | any     | depends       | Low-level runner with full flag control       |

**CLI structure built by the plugin:**
```
agent --mode=<agent|plan|ask>  --prompt "..."  [--model <model>]  [--yes]  [extraFlags]  <workspace>
```

**Configuration** (`appsettings.json → DotnetClaw:Cursor`):

| Key                              | Default   | Description                                                     |
|----------------------------------|-----------|-----------------------------------------------------------------|
| `ExecutablePath`                 | `"agent"` | Path to `agent.exe` / `agent`, or bare name if it's on PATH    |
| `DefaultTimeoutSeconds`          | `300`     | Per-invocation timeout (max 1800)                               |
| `RequireConfirmationForAgentMode`| `true`    | Prompt user before running in agent mode (destructive)          |
| `AutoApproveInAgentMode`         | `false`   | Pass `--yes` to suppress Cursor's own confirmations             |
| `Model`                          | `""`      | Override model, e.g. `"claude-3-5-sonnet"` or `"gpt-4o"`       |
| `ExtraFlags`                     | `""`      | Raw flags appended to every invocation                          |

**Finding `agent.exe` on your system:**
```
# Windows
%LOCALAPPDATA%\Programs\cursor\resources\app\bin\agent.exe

# macOS
/Applications/Cursor.app/Contents/Resources/app/bin/agent

# Linux
~/.local/share/cursor/resources/app/bin/agent
```
Set `ExecutablePath` in `appsettings.json` or add the binary's folder to your `PATH`.



Add a new skill in 3 steps:

1. Create `Plugins/MyPlugin.cs` with `[KernelFunction]` methods
2. Register in `KernelFactory.cs`:
   ```csharp
   builder.Plugins.AddFromObject(services.GetRequiredService<MyPlugin>(), "MySkill");
   ```
3. Add DI registration in `Program.cs`:
   ```csharp
   services.AddSingleton<MyPlugin>();
   ```

## MCP Client Skill

DotnetClaw connects to any [Model Context Protocol](https://modelcontextprotocol.io) server and exposes its tools directly to the Semantic Kernel agent — no hand-written glue code per tool.

**Packages used:**
- `ModelContextProtocol.Client 0.1.0-preview.10` — official .NET MCP SDK (stdio + SSE transports)
- `Microsoft.SemanticKernel.Plugins.MCP 1.30.0` — auto-converts MCP tool schemas → SK `KernelFunction`s via `IMcpClient.AsKernelPluginAsync()`

### How it works

```
appsettings.json: Mcp:Servers[]
        │
        ▼
McpConnectionManager (IHostedService)
  • Launches stdio servers as child processes (npx/uvx/python/custom binary)
  • OR connects to SSE servers over HTTP
  • Holds live IMcpClient instances, one per server
  • Connects in parallel at startup; failures are logged, not fatal
        │
        ▼
McpKernelLoader  (called from ClawAgentLoop.InitialiseAsync)
  • Calls IMcpClient.AsKernelPluginAsync("Mcp_{name}") for each connected client
  • Each MCP tool → SK KernelFunction with auto-generated description + parameters
  • Agent sees them identically to built-in skills
        │
        ▼
Agent turn: "Read the file /project/README.md"
  → Mcp_filesystem.read_file(path: "/project/README.md")
  → MCP server returns content
  → Agent gets the result
```

### Configuration

Enable servers in `appsettings.json` under `DotnetClaw:Mcp:Servers`:

```json
"Mcp": {
  "ConnectionTimeoutSeconds": 30,
  "LogToolCallDetails": false,
  "Servers": [
    {
      "Name": "filesystem",
      "Description": "Read/write local files",
      "Transport": "Stdio",
      "Command": "npx",
      "Arguments": [ "-y", "@modelcontextprotocol/server-filesystem", "/my/projects" ],
      "Enabled": true
    },
    {
      "Name": "github",
      "Description": "Search repos, issues, files on GitHub",
      "Transport": "Stdio",
      "Command": "npx",
      "Arguments": [ "-y", "@modelcontextprotocol/server-github" ],
      "Environment": { "GITHUB_PERSONAL_ACCESS_TOKEN": "ghp_..." },
      "Enabled": true
    },
    {
      "Name": "my-remote-server",
      "Description": "Custom SSE-based MCP server",
      "Transport": "Sse",
      "Url": "http://localhost:3000",
      "Enabled": true
    }
  ]
}
```

### Transport types

| Transport | How it connects | Use for |
|---|---|---|
| `Stdio` | Spawns a child process, communicates over stdin/stdout | Local tools: `npx`, `uvx`, Python scripts, custom binaries |
| `Sse` | HTTP long-lived connection (Server-Sent Events) | Remote servers, Docker containers, shared team servers |

### Popular public MCP servers (stdio)

| Server | Command | What it does |
|---|---|---|
| `@modelcontextprotocol/server-filesystem` | `npx -y @modelcontextprotocol/server-filesystem <path>` | Read/write/search files |
| `@modelcontextprotocol/server-github` | `npx -y @modelcontextprotocol/server-github` | Repos, issues, PRs |
| `@modelcontextprotocol/server-postgres` | `npx -y @modelcontextprotocol/server-postgres <conn>` | Read-only SQL |
| `mcp-server-fetch` | `uvx mcp-server-fetch` | Fetch + Markdown convert URLs |
| `@modelcontextprotocol/server-brave-search` | `npx -y @modelcontextprotocol/server-brave-search` | Web search |

### SK plugin naming

Each server becomes a plugin named `Mcp_{serverName}`. Special characters in server names are replaced with `_`:

```
Server "filesystem"  → plugin "Mcp_filesystem"
Server "my-github"   → plugin "Mcp_my_github"
```

The agent calls these exactly like built-in skills. With a filesystem server connected:

```
You: "List all .cs files in /src and find the one with the most lines"
→ Mcp_filesystem.list_directory(path: "/src")
→ [agent loops through results]
→ Mcp_filesystem.read_file(path: "/src/biggest.cs")
→ "The file with the most lines is ClawAgentLoop.cs (181 lines)"
```

### Management functions (McpPlugin)

| Function | Description |
|---|---|
| `mcp_list_servers` | List all servers, connection status, tool counts |
| `mcp_list_tools` | List tools + parameters for a specific server |
| `mcp_call_tool` | Call a tool with raw JSON args (debug/fallback) |
| `mcp_reconnect` | Reconnect a server and reload its tools into kernel |
| `mcp_list_resources` | List MCP resources exposed by a server |
| `mcp_read_resource` | Read a resource by URI |

## Browser Skill (Playwright)

DotnetClaw has a full headless browser via [Microsoft Playwright](https://playwright.dev/dotnet/).
The agent can navigate the web, take screenshots, fill forms, click buttons, and push screenshots directly to Telegram — all from natural language instructions.

### One-time setup

Install browser binaries after first `dotnet build`:

```bash
# Install the Playwright CLI tool
dotnet tool install --global Microsoft.Playwright.CLI

# Install Chromium (default). Add firefox or webkit if needed.
playwright install chromium
```

Or without the global tool:

```bash
pwsh bin/Debug/net10.0/playwright.ps1 install chromium
```

### Agent usage examples

```
You: "Go to https://github.com and take a screenshot"
→ browser_navigate(url: "https://github.com")
→ browser_screenshot_and_send()           ← sends photo to Telegram

You: "Log into the admin panel at https://app.example.com"
→ browser_navigate(url: "https://app.example.com/login")
→ browser_fill(cssSelector: "#username", value: "admin")
→ browser_fill(cssSelector: "#password", value: "secret")
→ browser_click(cssSelector: "button[type='submit']")
→ browser_screenshot_and_send(caption: "Login result")

You: "Fill the contact form and submit it"
→ browser_submit_form(
      fields: "#name=Alice\n#email=alice@example.com\n#message=Hello",
      submitSelector: "#send-btn",
      successSelector: ".thank-you-message")
```

### Telegram bot commands (Browser)

| Command | Description |
|---|---|
| `/goto <url>` | Navigate browser to URL, auto-send screenshot |
| `/screenshot` | Screenshot current page → Telegram photo |
| `/screenshot <selector>` | Screenshot a specific CSS element |

### Kernel functions

| Function | Description |
|---|---|
| `browser_navigate` | Navigate to a URL, returns title + status + load time |
| `browser_screenshot` | Save screenshot to disk, returns file path |
| `browser_screenshot_and_send` | Screenshot + send as Telegram photo in one step |
| `browser_get_text` | Extract visible text from page or element |
| `browser_fill` | Type value into a form field by CSS selector |
| `browser_click` | Click any element by CSS selector |
| `browser_submit_form` | Fill multiple fields + click submit atomically |
| `browser_evaluate` | Run JavaScript on the page, return the result |

### Configuration (`appsettings.json`)

```json
"Browser": {
  "BrowserType": "chromium",      // chromium | firefox | webkit
  "Headless": true,               // false to watch the browser window
  "DefaultTimeoutMs": 30000,
  "ScreenshotDirectory": "screenshots",
  "ScreenshotFormat": "png",      // png | jpeg
  "JpegQuality": 90,
  "PersistBrowserSession": true,  // reuse page between calls (cookies persist)
  "ViewportWidth": 1280,
  "ViewportHeight": 800,
  "SlowMoMs": 0                   // slow down ops (ms) — useful for debugging
}
```

### Architecture

```
BrowserSessionManager                 ← IHostedService, owns IPlaywright + IBrowser
    │  lazy-inits on first use
    │  persistent mode: reuses IPage (login state survives between turns)
    │  isolated mode: fresh IPage per call (clean state)
    │
    ▼
PlaywrightBrowserSession              ← IBrowserSession wrapping a real Playwright IPage
    │  navigate, screenshot, fill, click, evaluate, waitForSelector
    │
    ▼
BrowserPlugin                         ← 8 KernelFunctions
    │
    ├── browser_navigate
    ├── browser_screenshot         → saves to ./screenshots/
    ├── browser_screenshot_and_send → ITelegramBotClient.SendPhotoAsync (multipart upload)
    ├── browser_get_text
    ├── browser_fill
    ├── browser_click
    ├── browser_submit_form        → fills fields sequentially, then clicks submit
    └── browser_evaluate           → runs JS on page

TelegramCommandRouter
    ├── /goto <url>       → browser_navigate + browser_screenshot_and_send
    └── /screenshot [sel] → browser_screenshot_and_send
```

## Telegram Bot

Control DotnetClaw remotely via Telegram — no port forwarding or webhook required.
Uses **long-polling** (`getUpdates`) and **raw `HttpClient`** — zero Telegram SDK dependency.

### Setup

1. Message [@BotFather](https://t.me/BotFather) → `/newbot` → copy the token
2. Message [@userinfobot](https://t.me/userinfobot) to get your chat ID
3. Set config in `appsettings.json`:

```json
"Telegram": {
  "Enabled": true,
  "BotToken": "123456789:ABCdef...",
  "AllowedChatIds": [ 123456789 ]
}
```

Or via environment variable (recommended for production):
```bash
export TELEGRAM_BOT_TOKEN=123456789:ABCdef...
```

### Bot Commands

| Command | Description | Touches files? |
|---|---|---|
| `/ask <question>` | Ask DotnetClaw anything | ❌ |
| `<free text>` | Same as /ask | ❌ |
| `/plan <prompt>` | Cursor plan mode | ❌ |
| `/cursor_ask <q>` | Cursor Q&A about codebase | ❌ |
| `/agent <prompt>` | Cursor agent (edits files!) | ✅ |
| `/reset` | Clear conversation + reload workspace | — |
| `/status` | Show bot status | — |
| `/help` | Show command list | — |

### Configuration

| Key | Default | Description |
|---|---|---|
| `Enabled` | `false` | Must be `true` to activate |
| `BotToken` | `""` | Token from @BotFather (or `TELEGRAM_BOT_TOKEN` env var) |
| `AllowedChatIds` | `[]` | Whitelist of authorised chat IDs |
| `LongPollTimeoutSeconds` | `30` | Seconds per getUpdates call |
| `MaxMessageLength` | `4000` | Auto-split threshold (Telegram limit is 4096) |
| `ParseMode` | `MarkdownV2` | Message formatting mode |
| `SendTypingIndicator` | `true` | Show "typing…" while processing |

### Proactive Notifications (Agent Skill)

The agent can push Telegram messages mid-task using the `Telegram` kernel plugin:

```
You: "Run the tests and notify me on Telegram when done"
→ agent calls Shell.run_command("dotnet test")
→ agent calls Telegram.send_telegram_notification("Tests Complete", "12 passed, 0 failed")
→ 📱 You receive a Telegram message immediately
```

### Architecture

```
Telegram long-poll (getUpdates, 30s)
        │
        ▼
TelegramPollingService        ← IHostedService, runs alongside the REPL
        │
        ├── AllowedChatIds whitelist check
        ├── Per-chat SemaphoreSlim   (serialises concurrent messages)
        ├── SendChatAction "typing…" (instant feedback)
        │
        ▼
TelegramCommandRouter         ← Parses /commands and free text
        │
        ├── /ask + freetext → ClawAgentLoop.RunTurnAsync(outputSink: ResponseCollector)
        ├── /plan           → CursorPlugin.CursorPlanAsync
        ├── /agent          → CursorPlugin.CursorAgentAsync
        ├── /cursor_ask     → CursorPlugin.CursorAskAsync
        └── /reset /status /help → inline string responses
        │
        ▼
ITelegramBotClient            ← Raw HttpClient, no SDK
  sendMessage (MarkdownV2, auto-splits >4000 chars, retries as plain text on parse error)
```

## Solution structure

```
DotnetClaw/
├── workspace/                      ← Identity documents (loaded every session)
│   ├── SOUL.md                     ← Agent personality & values
│   ├── AGENTS.md                   ← Tool-use rules & orchestration behaviour
│   ├── USER.md                     ← User profile & preferences
│   ├── CONTEXT.md                  ← Current project / session context
│   ├── MEMORY.md                   ← Persistent facts across resets
│   └── <custom>.md                 ← Any additional documents
├── src/DotnetClaw/
│   ├── Program.cs                  ← Entry point + REPL loop
│   ├── appsettings.json            ← Configuration
│   ├── Config/
│   │   └── DotnetClawOptions.cs    ← Typed config model
│   ├── Workspace/
│   │   ├── WorkspaceDocument.cs    ← Typed record for a loaded identity doc
│   │   └── WorkspaceLoader.cs      ← Scans ./workspace, loads in priority order
│   ├── Agents/
│   │   ├── ClawAgentLoop.cs        ← Core agentic loop (SK ChatCompletionAgent)
│   │   └── KernelFactory.cs        ← Kernel + plugin wiring, provider selection
│   ├── Telegram/
│   │   ├── TelegramModels.cs         ← TelegramUpdate, Message, Chat, User, ApiResponse<T>
│   │   ├── TelegramBotClient.cs      ← ITelegramBotClient + raw HttpClient impl
│   │   ├── TelegramCommandRouter.cs  ← Command parser + dispatch to agent/Cursor
│   │   └── TelegramPollingService.cs ← IHostedService long-poll loop
│   │   ├── ShellPlugin.cs          ← run_command, list_directory
│   │   ├── FileSystemPlugin.cs     ← read/write/find files
│   │   ├── DotnetPlugin.cs         ← .csproj / C# project analysis
│   │   ├── WorkspacePlugin.cs      ← Runtime workspace query skill
│   │   ├── CursorPlugin.cs         ← cursor_agent / cursor_plan / cursor_ask / cursor_run
│   │   ├── CursorTypes.cs          ← CursorMode enum, CursorResult
│   │   ├── CursorProcessRunner.cs  ← ICursorProcessRunner + real OS process impl
│   │   └── TelegramPlugin.cs       ← send_telegram_message, send_telegram_notification
│   └── UI/
│       └── SpectreConsoleRenderer.cs ← Rich terminal UI via Spectre.Console
└── tests/DotnetClaw.Tests/
    ├── ShellPluginTests.cs
    ├── FileSystemPluginTests.cs
    ├── WorkspaceLoaderTests.cs
    ├── CursorPluginTests.cs        ← FakeCursorProcessRunner, all modes + edge cases
    ├── TelegramBotClientTests.cs   ← MockHttpMessageHandler, send/receive/split
    └── TelegramCommandRouterTests.cs ← Command parsing, routing, Markdown escaping
```

## License

MIT
