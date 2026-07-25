# ADR 0003: Isolate @cline/agents behind AgentRuntimeAdapter

- Status: Accepted
- Date: 2026-07-25

## Context

LIGClaw needs a tool-using agent loop but must not inherit coding-agent filesystem, shell, session, or persistence behavior. The selected Cline packages are experimental 0.0.x APIs and can change between releases.

## Decision

- Pin `@cline/agents`, `@cline/llms`, and `@cline/shared` exactly to `0.0.65`.
- Use `@cline/agents` rather than the `@cline/sdk` alias because it supplies the loop, events, hooks, cancellation, restore, and custom Tool surface without host default Tools or persistence.
- Hide Cline event and result types behind LIGClaw's `AgentRuntimeAdapter` and normalized `AgentEvent` contract.
- Keep a deterministic Replay adapter and a deterministic `AgentModel` exercising the real Cline loop in CI.
- Add real provider configuration only after Desktop-owned secret storage and settings exist. Provider credentials must not be persisted or logged by Sidecar.
- Require contract, replay, and process integration tests before upgrading the pinned Cline packages.

## Consequences

- Desktop and persisted events are independent of Cline's experimental type shapes.
- Phase 0 can verify streaming and cancellation without API credentials or nondeterministic network calls.
- The Cline dependency graph currently carries one low-severity transitive advisory through `dify-ai-provider`. CI fails on moderate-or-higher advisories; the low advisory remains tracked until the pinned upstream package provides a compatible patched dependency.
