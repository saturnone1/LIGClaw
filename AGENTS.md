# LIGClaw agent guide

LIGClaw is a local-first Windows assistant. Read `docs/IMPLEMENTATION_PLAN.md` before architectural changes.

## Verify

Run `./scripts/verify.ps1` from the repository root. It installs the locked sidecar dependencies, checks TypeScript, builds the sidecar, formats-checks .NET, and runs all .NET tests.

## Architecture rules

- Domain and Application must not depend on WPF, SQLite, Cline, MCP, or Windows adapters.
- Desktop owns Windows effects, approval decisions, scheduling, secrets, and SQLite.
- Sidecar owns only the replaceable agent runtime and MCP sessions.
- Cross-process and Tool payloads are schema-first under `contracts/`.
- Never expose arbitrary shell execution by default.
- Any destructive or external side effect must pass the Desktop policy pipeline.
- Add a deterministic contract/replay test for every protocol bug.

## Editing rules

- Keep changes as a small vertical slice with its tests.
- Do not edit generated contracts independently in C# and TypeScript.
- Record changes to process boundaries, permissions, storage, or dependencies in `docs/adr/`.
- Never put prompts, clipboard contents, file contents, credentials, or tokens in logs.
