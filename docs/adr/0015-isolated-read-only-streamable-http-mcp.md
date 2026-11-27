# ADR 0015: Isolated read-only Streamable HTTP MCP foundation

## Status

Accepted on 2026-07-26.

## Context

Phase 5 requires MCP connectivity without weakening the Desktop-owned policy boundary. MCP servers are external and may be unavailable, malicious, or expose operations whose annotations cannot be trusted. A stdio configuration would also let configuration data select an executable, which conflicts with the rule that arbitrary shell execution is disabled by default.

The official TypeScript SDK documents the v1 line as stable while v2 remains pre-alpha. The current stable v1 package is used instead of building a private transport implementation.

## Decision

- Sidecar owns an isolated MCP client session per configured server and implements the schema-first `mcp.configure`, `mcp.status`, and `mcp.call` RPC methods in protocol 1.7.
- Remote Streamable HTTP endpoints require HTTPS; plaintext HTTP is limited to loopback. URL credentials and fragments are rejected. Optional Bearer tokens are Desktop-owned Windows credentials and are never returned by status RPCs.
- Configuration is read-only, limited to eight connections, 128 discovered tools per connection, five pagination requests, and bounded connect, list, and health operations.
- One server's connection, discovery, health, or close failure changes only that connection's state.
- Desktop persists non-secret connection metadata under the current user's registry and shows connection/tool-discovery status in Settings.
- Discovered tools are inert until the user explicitly allowlists their exact names. Allowed tools are exposed through one generic model Tool, but Desktop independently revalidates the connection/tool pair, assigns R3, shows the external destination and arguments, and requires approval before Sidecar can call it.
- stdio accepts no arbitrary command configuration. The initial executable registry contains only the bundled deterministic `ligclaw-sample-rag` ID; Sidecar resolves that ID to bundled Node and its own bundled script.
- Results are treated as untrusted external content, text/JSON-only and bounded to 64 KiB. Inputs are bounded to 32 KiB.
- `@modelcontextprotocol/sdk` is pinned to `1.29.0`. Its transitive HTTP server dependency is overridden to `@hono/node-server` `2.0.12` to avoid the vulnerable 1.x range; LIGClaw uses the SDK client path only.

## Consequences

- A local or HTTPS read-only RAG server can be configured and monitored without exposing its tools to the agent prematurely.
- MCP startup does not block model configuration, Windows tools, scheduling, or local management pages.
- Authenticated HTTP and fixed-registry stdio connections, safely classified invocation, the bundled sample RAG server, diagnostics, packaging/update automation, and soak/security acceptance are implemented. Release acceptance still requires the signed package lifecycle and login-startup check on a clean Windows account whose machine trusts the release signing chain.
