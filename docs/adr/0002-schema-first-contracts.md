# ADR 0002: Schema-first RPC and Tool contracts

- Status: Accepted
- Date: 2026-07-25

## Context

The Desktop and Sidecar use different languages. Independently maintained DTOs drift easily, and an LLM Tool description that differs from execution validation is a safety issue.

## Decision

- Keep canonical RPC and Tool schemas under `contracts/`.
- Version externally meaningful Tool names, for example `system.get_status.v1`.
- Reject unknown or invalid payloads at the process boundary.
- Generate C# and TypeScript contract types once the schema set grows beyond the Phase 0 bootstrap.
- Gate generated-code drift and replay fixtures in CI.

## Consequences

- Contract changes become explicit and reviewable.
- The Phase 0 bootstrap has matching hand-written DTOs plus framing tests; schema code generation is the next infrastructure slice.
