# MEMORY — Persistent Facts

This file is used to store facts, decisions, and context that should persist
across conversation resets. Edit or append to this file manually, or ask
DotnetClaw to write to it using the `write_file` or `append_file` skill.

## Architectural Decisions

<!-- Record important design decisions here so they survive session resets -->

- Chose Microsoft Semantic Kernel over raw OpenAI SDK for better plugin/agent abstraction
- Workspace documents are injected before the base system prompt to allow identity to override defaults
- `WorkspaceLoader` uses a priority queue: SOUL → AGENTS → USER → CONTEXT → custom

## Useful Commands

```bash
# Build the solution
dotnet build DotnetClaw.sln

# Run tests
dotnet test tests/DotnetClaw.Tests

# Run the assistant
cd src/DotnetClaw && OPENAI_API_KEY=sk-... dotnet run
```

## Notes

<!-- Add session notes here -->

- (empty)
