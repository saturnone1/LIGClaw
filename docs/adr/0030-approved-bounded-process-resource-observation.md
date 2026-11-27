# ADR 0030: Require context approval for bounded process resource observation

- Status: accepted
- Date: 2026-08-01

## Context

Overall CPU and memory percentages do not explain why a PC feels slow. Process names provide useful attribution, but they can reveal which business applications a user is running. Process IDs, executable paths, window titles, account names, and command lines would add more sensitive context than this diagnosis needs.

## Decision

Protocol 1.15 adds `system.get_process_resource_status.v1` as an R1 Tool with a required reason and a caller-selected limit from 1 to 10. Desktop samples process CPU time for approximately 500 ms and reads working-set memory using the common Windows process API on Windows 10 and Windows 11.

Results are grouped case-insensitively by bounded process name so multi-process applications do not fill the list. Separate CPU and memory rankings contain at most the approved number of groups. PID and process start time are used only inside Desktop to reject PID reuse between samples and never cross the process boundary. Paths, command lines, window titles, user names, and persistent identifiers are not read or returned.

The approval explains that process names and momentary usage will be sent to the model. It has no persistent grant scope, so each request requires context approval. Protected or exited processes are skipped independently; a provider-level failure becomes a structured `unavailable` result.

## Consequences

- Users can ask which apps are making the PC slow without granting process-control authority.
- The Tool cannot terminate, reprioritize, inspect command lines, or manage services.
- Deterministic aggregation, PID-reuse, bounds, privacy, Windows 10/11 adapter, and Sidecar replay tests protect the contract. Real workload behavior remains in the external OS acceptance matrix.
