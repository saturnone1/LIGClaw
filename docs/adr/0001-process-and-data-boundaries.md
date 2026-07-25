# ADR 0001: Desktop owns effects and data; Sidecar is replaceable

- Status: Accepted
- Date: 2026-07-25

## Context

LIGClaw needs Windows session APIs and a JavaScript agent ecosystem. Putting both concerns in one process would either weaken native integration or couple all product behavior to an experimental agent SDK. Allowing Desktop and Sidecar to share SQLite would add locking, migration ownership, and recovery ambiguity.

## Decision

- Use a .NET 10 WPF Desktop process for UI, Windows effects, policy, approval, scheduling, secrets, and persistence.
- Use a child Node.js Sidecar for the replaceable agent runtime and MCP sessions.
- Communicate over a per-launch Windows Named Pipe using framed JSON-RPC 2.0.
- Desktop creates the pipe, starts the child with an unlogged one-time token, and validates protocol version plus contract hash.
- Desktop is the only SQLite owner. Sidecar requests durable operations over a contract.
- Sidecar termination must not terminate Desktop; Desktop supervises and restarts it with crash-loop protection.

## Consequences

- Agent SDK upgrades are contained in a runtime adapter.
- Windows execution policy cannot be bypassed by Sidecar implementation details.
- Cross-process contracts need explicit versioning and compatibility tests.
- Some data operations require an extra RPC hop, which is acceptable for assistant workloads.
