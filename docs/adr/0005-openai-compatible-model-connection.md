# ADR 0005: Support one configurable OpenAI-compatible model connection

## Status

Accepted for Phase 1 on 2026-07-25.

## Decision

- Expose only three model connection fields: Base URL, API Key, and Model.
- Do not hard-code or privilege a vendor, endpoint, key prefix, or model name.
- Store Base URL and Model under the current user's LIGClaw registry settings; store the API key as a Generic Credential in Windows Credential Manager.
- Send credentials to the Sidecar only over the current-user Named Pipe with `provider.configure` or `provider.test`. The Sidecar retains the active connection in memory and never logs the payload.
- Use the Cline `openai` provider with its custom `baseUrl` path so connection tests and conversations exercise the same OpenAI-compatible runtime.

## Consequences

- Other vendor-specific authentication and online provider selection flows are intentionally out of scope.
- A connection test performs a minimal model request and may incur provider usage.
- No API key is written to JSON, SQLite, command-line arguments, environment variables, diagnostics, or source control.
- Contract version 1.2 is required because provider configuration crosses the Desktop/Sidecar process boundary.
