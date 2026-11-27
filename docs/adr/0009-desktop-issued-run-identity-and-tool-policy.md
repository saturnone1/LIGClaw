# ADR 0009: Desktop-issued run identity and Tool authorization policy

## Status

Accepted for Phase 1 on 2026-07-25.

## Decision

- Protocol 1.4 requires Desktop to create both `conversationId` and `runId` before `conversation.start`.
- Sidecar must echo that `runId`, reject a duplicate active run ID, and include it in every Agent event and Tool request.
- Desktop accepts Agent events and Tool calls only when both identifiers match the active run.
- A process-local Application policy rejects duplicate `toolCallId` values and requires explicit authorization for R1-R4 calls. R0 calls remain automatic.
- The Windows Tool host validates canonical adapter name, capability, and risk, but no longer owns approval policy.

## Rationale

Checking only `conversationId` leaves a confused-deputy window where a late or compromised Sidecar can attach a Tool request to the current conversation with an unrelated run. Generating the run identity in Desktop makes the trust boundary explicit before any Sidecar work starts.

Keeping authorization outside the Windows adapter host also avoids hard-coding the temporary Phase 1 “R0 only” rule into execution code. The approval UI can later grant one concrete invocation without weakening adapter validation.

## Consequences

- Protocol 1.3 peers are intentionally incompatible and fail during the existing handshake.
- Tool calls are at-most-once within one active run; a repeated ID is rejected even if the first execution failed.
- A delayed event or Tool request from an ended run is ignored and is not written into that conversation history.
