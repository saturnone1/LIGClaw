# ADR 0007: Persist conversations in a Desktop-owned SQLite store

## Status

Accepted for Phase 1 on 2026-07-25.

## Decision

- Store conversations and normalized agent events in `%LOCALAPPDATA%\LIGClaw\Data\ligclaw.db`.
- Desktop exclusively owns the SQLite connection, WAL setup, schema migrations, and recovery of interrupted runs. Sidecar never opens the database.
- Keep one serialized connection for streaming writes and make `(run_id, sequence)` unique so replayed events are idempotent.
- Mark conversations left in `running` state as `interrupted` on the next app start.
- Keep credentials, prompts sent only to diagnostics, and provider secrets out of this database. The user request and assistant text are local conversation history and are intentionally persisted.
- Use `Microsoft.Data.Sqlite` 10.0.10. Pin `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12 directly because the provider's default 2.1.11 native dependency is deprecated and has a high-severity advisory.

## Consequences

- Recent conversations can be restored without asking the model again.
- Storage failures degrade the history feature and are diagnosed without blocking a new model request.
- Schema changes require ordered migrations and reopen tests.
- Retention controls, deletion, export, and encrypted enterprise storage policy remain explicit follow-up work before broader deployment.
