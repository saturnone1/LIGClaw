# ADR 0028: Keep release evidence bounded and separate hardware acceptance

- Status: accepted
- Date: 2026-08-01

## Context

The local release gate needs durable proof for repeated MCP sessions, Desktop-owned Sidecar recovery, persistence recovery, resume reconciliation, and diagnostic redaction. Raw command transcripts can expose machine paths or future sensitive diagnostic content, while forcing physical sleep from an unattended build can interrupt user work and cannot prove Windows 10 and Windows 11 behavior on one host.

## Decision

`collect-release-evidence.ps1` runs the automatable checks and writes a schema-versioned JSON document containing only check names, pass/fail status, bounded durations and iteration counts, the Git revision, and whether tracked files differ. It does not capture command output, environment variables, usernames, or absolute paths.

Physical sleep/resume is recorded as `manual-required`. It remains part of the external Windows 10/11 acceptance matrix and is never initiated by the release script. A release candidate includes the evidence document and its SHA-256 only when every automated check passed.

The Desktop restart soak refuses to run when a user-owned LIGClaw process is already active. Release automation does not terminate that process.

## Consequences

- One local command produces machine-readable automated evidence without expanding diagnostic data collection.
- The MSIX manifest can bind the candidate to the exact evidence file by hash.
- A candidate still cannot be called fully accepted until physical sleep/resume and the remaining OS/signing matrix are completed externally.
