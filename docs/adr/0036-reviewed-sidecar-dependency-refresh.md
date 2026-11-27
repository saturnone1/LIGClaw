# ADR 0036: Reviewed Sidecar dependency refresh

Date: 2026-11-27
Status: Accepted for the development branch; production release gates remain open.

## Context

PR #1 contains the v0.6.0 integrated assistant, not only the initial resident-shell
slice described by its original title. Its older lockfile reports 15 npm audit
findings (including a critical transitive finding) on the review date.

## Decision

Keep the Cline runtime contract packages pinned at 0.0.65. Upgrade the MCP SDK
from 1.29.0 to 1.32.2 and its Hono server override to 2.1.11. Refresh compatible
transitive packages and override undici to 6.29.1 to remove the vulnerable
legacy undici 5 / busboy dependency introduced by Dify's provider utilities.
The project already requires Node 24. Keep these dependency changes separate
from the README conflict resolution and feature implementation.

Use the existing generated-contract, TypeScript, Node, named-pipe integration,
and Desktop tests to check compatibility. No provider credentials are needed
for these deterministic tests. Local registry settings are environmental: use
public registry/source overrides for review without changing user settings.

## Remaining release gate

Audit now reports four high package findings for one advisory:
[GHSA-mrhx-c7xc-m3w5](https://github.com/advisories/GHSA-mrhx-c7xc-m3w5),
through @cline/agents -> @cline/llms -> dify-ai-provider -> zod 3.25.76.
The advisory's patched version is Zod 4.6.6; the latest Dify provider 1.1.1
still requires Zod 3. A forced Zod major upgrade is not accepted as a safe fix.
The current Cline 0.2.2 dependency graph also retains Dify, so a runtime
major/minor migration alone does not remove this path.

LIGClaw configures the runtime with the fixed `openai-compatible` provider ID
in cline-agent-runtime-adapter.ts; Dify cannot be selected through its model
settings. This reduces direct exposure of the unused provider path but does
not establish that the dependency is generally safe. No advisory allowlist,
severity downgrade, or audit bypass is added. `npm audit --audit-level=moderate`
and therefore the aggregate verify script still fail until this upstream
path is replaced or patched compatibly. Development integration is not a
production-release approval. Recheck this finding before packaging a release.

## Verification on the review PC

- Clean npm install, generated contract check, TypeScript check/build, and 84 Node tests pass.
- .NET format and Debug build pass; Contracts 7, Application 20, Desktop 389, and Sidecar integration 1 tests pass.
- Actual-process Sidecar handshake, heartbeat, restart, and cleanup smoke passes.
- Clean Windows account installation/signing, Windows 10 hardware, and live-provider testing remain external gates.
