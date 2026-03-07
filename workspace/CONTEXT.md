# CONTEXT — Current Session Context

## Active Project

<!-- Update this file when switching projects. It is loaded fresh every session. -->

**Project**: DotnetClaw
**Description**: OpenClaw-inspired AI assistant built on .NET 10 and Microsoft Semantic Kernel.
**Solution file**: `DotnetClaw.sln`
**Main entrypoint**: `src/DotnetClaw/Program.cs`

## Architecture Notes

- Agent loop: `ClawAgentLoop` — wraps SK `ChatCompletionAgent` with an iteration guard
- Plugins: Shell, FileSystem, Dotnet, Workspace (all registered via DI → KernelFactory)
- Identity docs: loaded from `./workspace/` at session start via `WorkspaceLoader`
- UI: `SpectreConsoleRenderer` — Figlet banner, streaming token output, tool-call panels

## Current Focus

<!-- Describe what you're working on right now -->

- Initial skeleton setup and workspace identity document support

## Known Issues / TODOs

- [ ] Anthropic provider not yet wired in `KernelFactory.cs`
- [ ] Add hot-reload file watcher to `WorkspaceLoader`
- [ ] Add `MEMORY.md` auto-write at end of session
- [ ] Streaming chunk rendering needs colour differentiation for tool vs text output
