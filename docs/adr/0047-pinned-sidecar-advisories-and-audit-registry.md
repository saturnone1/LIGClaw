# ADR 0047: Pin patched Sidecar transitive dependencies and separate the audit registry

## Context

The 2026-08-11 release audit found that the configured internal Nexus npm proxy accepted package downloads but returned HTTP 400 for npm's advisory API. A direct audit-capable registry also reported `fast-uri@3.1.4` at high severity and `hono@4.12.32` at moderate severity. Both packages enter through pinned Sidecar dependencies. The previously tracked `dify-ai-provider` copy of `@ai-sdk/provider-utils@3.0.30` remains a low-severity advisory with no compatible patched 3.x release.

Changing Cline or MCP major behavior during release stabilization would expand the runtime and protocol risk. Ignoring moderate-or-higher findings or treating an unsupported Nexus audit endpoint as a clean result would also be unsafe.

## Decision

- Keep `@cline/agents`, `@cline/llms` and `@cline/shared` pinned at `0.0.65` and `@modelcontextprotocol/sdk` pinned at `1.29.0` for this release.
- Pin `fast-uri` to `3.1.5` and `hono` to `4.13.1` with npm overrides. Keep the existing `@hono/node-server@2.0.12` override.
- Run locked installation against the operator-configured npm registry.
- Run advisory lookup against the explicit `NpmAuditRegistry` accepted by `scripts/verify.ps1`; its default is the public npm advisory service. A fully disconnected build must pass an approved internal registry that implements npm audit metadata.
- Continue to fail verification on moderate-or-higher advisories. An unavailable advisory service is not interpreted as a clean audit.
- Track the remaining low advisory separately and do not force an incompatible `@ai-sdk/provider-utils` major override into the unused Dify provider path.

## Consequences

The application remains reproducible from the lockfile and keeps the already accepted Cline adapter behavior. Internal package download policy remains independent from advisory metadata availability. Air-gapped release builders need an imported advisory feed or an audit-capable internal endpoint; without it they cannot claim a current vulnerability audit. Every override change must rerun Sidecar tests, Named Pipe integration tests, the full .NET suite and release packaging.
