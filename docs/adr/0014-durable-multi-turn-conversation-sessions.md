# ADR 0014: Durable multi-turn conversation sessions

## Status

Accepted on 2026-07-26.

## Context

Desktop previously generated a new `conversationId` for every send action and Sidecar created a new Cline `AgentRuntime` for every run. Recent-conversation rows therefore represented individual prompts rather than threads, follow-up prompts lost model context, and selecting an earlier conversation could not continue it.

## Decision

- A conversation is a durable thread with one stable Desktop-issued `conversationId`.
- Every user turn has a distinct Desktop-issued `runId`; Tool authorization remains scoped to the exact conversation/run pair.
- Desktop persists each run's user input, accumulated assistant text, status, and timestamps in `conversation_runs`.
- `conversation.start` protocol 1.5 carries at most 40 previously completed user/assistant messages. This allows Sidecar or app restart recovery without making Sidecar the durable owner.
- Sidecar reuses one Cline `AgentRuntime` per active conversation and updates the run-scoped Tool bridge identity before every run.
- Provider reconfiguration discards inactive process-local runtime sessions. An active run is never aborted by a settings change; its session is retired after completion and the next run is restored from Desktop history under the new provider configuration.
- A new conversation is created only through the explicit new-conversation action or when no conversation is selected.

## Safety and limits

- Only completed textual turns are restored into model context; partial failed/cancelled Tool state is not replayed.
- History is bounded to 40 messages and 64,000 characters by Desktop, and each protocol message is capped at 20,000 characters.
- History is sent only to the provider already selected for that user-initiated conversation request and is never written to logs.
- Tool closures cannot retain an earlier `runId`; a scoped bridge overwrites conversation/run identity at invocation time.
- A delayed failure update targets only its exact `runId` and cannot overwrite a newer running turn's conversation status.
- A failed cancellation RPC does not end the Desktop run locally; only a terminal agent event or connection loss establishes that boundary.

## Consequences

- Protocol 1.4 peers are intentionally incompatible and fail the existing handshake.
- Existing single-prompt rows migrate into one legacy run without losing visible assistant text.
- Recent conversations now count and continue turns rather than creating one row per prompt.
