# LIGClaw

A local-first Windows assistant with a WPF desktop host and a Node.js
sidecar connected through per-user, versioned JSON-RPC Named Pipes.

The integrated development baseline is **v0.6.0**. It includes conversation,
the resident Windows shell, approved Windows tools, local memory and
scheduling, bounded UI Automation, and MCP integration. It is a beta, not a
fully validated release: installation, login startup, upgrade, and removal
on clean Windows accounts with a trusted signing chain remain external
release gates. Windows 10 deployment support also awaits hardware validation.

The [capability audit](docs/WINDOWS_AGENT_CAPABILITY_AUDIT.md) distinguishes
implemented, partial, planned, and follow-up functionality. The current
priority is [release hardening and maintainability](docs/C_STAGE_PLAN.md).

## Implemented scope

- Single-instance desktop shell, tray, configurable quick-access shortcut
  (default `Ctrl + Alt + Space`), and opt-in current-user login startup.
- Streaming conversation, cancellation, recent conversation history, and
  Markdown responses. Desktop owns SQLite persistence.
- Named OpenAI-compatible model profiles and bounded fallback routing;
  API keys are held in Windows Credential Manager. Model requests are sent
  to the endpoint configured by the user.
- Desktop-owned Windows tool policy, approval, bounded execution, and audit.
  Capabilities include system diagnostics, registered applications, windows,
  scoped files and clipboard, explicit memory, reminders, and agent jobs.
- MCP Streamable HTTP and a fixed-ID bundled stdio sample, with exact-name
  tool allowlists and Desktop approval for calls.
- User-driven screen context, bounded browser/UI Automation, and opt-in
  speech features; see the capability audit for limits and unfinished work.

Domain and Application remain independent of Windows, WPF, SQLite, Cline,
and MCP adapters. Desktop is the final authority for external effects and
persistence; Sidecar owns the replaceable agent runtime. Arbitrary shell
execution and permanent deletion are not exposed as normal tools.

## Requirements

- Windows 10/11; technical minimum target is Windows 10 1809 (build 17763).
  Supported deployment combinations are subject to the release gates above.
- .NET SDK 10.0.103 or a later patch in that SDK feature band (`global.json`).
- Node.js 24.
- PowerShell 7 or Windows PowerShell 5.1.

Conversation data lives under `%LOCALAPPDATA%\LIGClaw\Data\ligclaw.db`.
API keys are stored separately in Windows Credential Manager.

## Verify and run

```powershell
./scripts/verify.ps1
./scripts/run.ps1
```

Verification covers PowerShell 5.1 parsing, release evidence helpers,
generated contract drift, TypeScript, Node tests, dependency audit, .NET
formatting, builds, and tests. The run script builds Sidecar and opens Desktop.
Closing the window keeps the app in the tray; use the tray menu to exit.

The unused Dify provider and its Zod 3 dependency have been removed from the
model SDK through reproducible local packages. The embedded Zod collection
size guard is also backported into the Agents, Models and Shared SDK bundles.
Both development and packaged production restores use those packages. See [the dependency decision](docs/adr/0037-remove-unused-dify-provider.md)
and [vendor provenance](sidecar/vendor/README.md). The dependency audit gate
remains enabled without advisory suppression.

With no existing LIGClaw instance running, the following exercises the real
process handshake, heartbeat, restart after a forced Sidecar kill, and cleanup:

```powershell
./scripts/smoke-sidecar.ps1
```

## Structure and documentation

```text
src/LIGClaw.Desktop       WPF composition, orchestration, and lifecycle
src/LIGClaw.Application   use cases and ports
src/LIGClaw.Domain        domain policies
src/LIGClaw.Contracts     generated RPC contracts and framing
sidecar                  Replay/Cline runtime and MCP adapters
contracts                schema-first RPC and tool definitions
tests                    deterministic contract, policy, and adapter tests
```

Start with the [handoff](docs/HANDOFF.md), [implementation plan](docs/IMPLEMENTATION_PLAN.md),
[design system](docs/DESIGN_SYSTEM.md), and [architecture decisions](docs/adr/).
Contract version/hash mismatches are rejected. IPC is bounded to a 4 MiB
payload and 8 KiB header; sensitive content is passed only through the
approved context/resource paths documented in the capability audit.
