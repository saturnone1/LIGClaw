# LIGClaw

A local-first personal assistant that lives in the Windows user session and takes over
repetitive work without handing anything to a remote service.

**Status: Phase 0 walking skeleton.** A WPF desktop app owns the lifecycle of a Node.js
sidecar and performs a versioned JSON-RPC handshake plus heartbeat over a per-user Windows
named pipe. That is the whole of it so far.

## What actually works today

The only sidecar capability is `health.ping`.

Cline/LLM integration, conversation, Windows tooling, the tray, memory, scheduled tasks
and MCP are **not implemented yet** — they are not disabled, gated or paywalled. There are
no license, account or per-model feature flags, and no call quotas.

The following *are* deliberate limits, but they exist for process stability and security
rather than to restrict functionality:

| Boundary | Value |
|---|---|
| IPC payload / header ceiling | 4 MiB / 8 KiB |
| Connect timeout | 10 s |
| Heartbeat response timeout | 5 s |
| Named pipe access | same Windows user only |
| Sidecar with mismatched contract version or hash | connection refused |
| Diagnostics UI | 100 entries, 2,048 chars each |

Screenshots and large files will not be pushed through IPC directly. When that work
lands, they will be passed as approved temporary resource handles so these boundaries
still hold.

## Requirements

- Windows 10/11
- .NET SDK 10.0.103 or a later patch
- Node.js 24
- PowerShell 7, or Windows PowerShell 5.1

## Verify

```powershell
./scripts/verify.ps1
```

## Run

```powershell
./scripts/run.ps1
```

This builds the sidecar first, then launches the desktop app. The window shows runtime
connection state and a bounded diagnostic log, and lets you exercise restart behaviour.

To exercise the real process handshake, heartbeat, automatic restart after a forced kill,
and orphan cleanup:

```powershell
./scripts/smoke-sidecar.ps1
```

## Layout

```text
src/LIGClaw.Desktop       WPF UI and the sidecar supervisor
src/LIGClaw.Application   use cases and ports (to be expanded)
src/LIGClaw.Domain        pure domain model (to be expanded)
src/LIGClaw.Contracts     JSON-RPC contract and framing
sidecar                   replaceable Node.js agent runtime boundary
contracts                 schema-first RPC / tool contracts
tests                     deterministic contract tests
```

## Design notes

Scope and phasing live in the [implementation plan](docs/IMPLEMENTATION_PLAN.md);
the decisions behind the structure are recorded as [ADRs](docs/adr/).
