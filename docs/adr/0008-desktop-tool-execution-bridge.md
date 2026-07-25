# ADR 0008: Execute Windows Tools only through the Desktop bridge

## Status

Accepted for Phase 1 on 2026-07-25.

## Decision

- Protocol 1.3 adds Sidecar `tool.invoke` notifications and Desktop `tool.result` requests correlated by `toolCallId`, `conversationId`, and `runId`.
- Sidecar exposes model-safe tool names such as `system_get_status`, then maps them to versioned canonical Desktop names such as `system.get_status.v1`.
- Desktop validates the active conversation, canonical allowlist, declared risk, and platform capabilities before it executes any Windows API.
- Tool adapters declare required capabilities and priority. The host selects the highest-priority compatible adapter, allowing Windows 10 and Windows 11 implementations to coexist without version checks in agent code.
- Only R0 read-only Tools can auto-run in this slice. R1 and above are rejected until the approval UI and policy persistence path exists.
- Tool waits have timeout and cancellation behavior. Sidecar disconnect rejects every pending call.

## Consequences

- Sidecar and model output cannot directly invoke Win32, shell commands, or arbitrary executables.
- `system.get_status.v1` uses the common Windows 10/11 desktop API path and returns only release, build, architecture, local time, time zone, and power source.
- A compromised or stale Sidecar request cannot execute a Tool outside the currently active conversation.
- New Windows Tools require a schema, Desktop adapter, capability declaration, policy risk, deterministic bridge test, and Windows 10/11 compatibility test.
