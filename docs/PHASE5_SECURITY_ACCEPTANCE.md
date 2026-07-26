# Phase 5 security, fault, and performance acceptance

Updated 2026-07-26.

## Automated evidence

- MCP configuration: maximum eight connections, 128 discovered tools, five pages, 32 locally allowlisted tools.
- Transport: remote Streamable HTTP requires HTTPS; HTTP is loopback-only. URL credentials/fragments and authorization header newline injection are rejected.
- Authentication: Bearer tokens are stored in Windows Credential Manager, transferred only in the local authenticated pipe configuration request, held in memory, and omitted from status/results/diagnostics.
- stdio: no command/path/arguments field is accepted. Sidecar maps only the compiled `ligclaw-sample-rag` executable ID to bundled Node and the bundled server script.
- Calls: Desktop rechecks connection/tool allowlists, classifies every external call as R3, shows destination/tool/arguments, and requires one-time approval. Audit summaries omit arguments and response text.
- Results: only text and structured JSON are accepted, capped at 65,536 characters. Arguments are capped at 32 KiB. Binary/resource payloads are not forwarded.
- Isolation: failed discovery/health/call affects only its connection; slow calls do not serialize another connection. Reconfiguration retires active model sessions only after the current run.
- Process recovery: pipe error/close has a two-second MCP cleanup grace period and Sidecar monitors its Desktop parent. Handshake/restart/orphan smoke passes.
- Diagnostic bundle: contains only a platform/version manifest and the latest 100 bounded diagnostic lines. Credential patterns, NVIDIA keys, and the user profile path are redacted; SQLite, prompts, clipboard, file contents, and tokens are excluded.
- Soak evidence: 20 real stdio sample RAG connect/call/close cycles completed with no retained heap growth. Ten forced Sidecar restarts completed with Desktop private-memory growth 13,438,976 bytes and handle growth 42, below 128 MiB/256 limits.
- Packaging: MakeAppx produced a 149,972,894-byte x64 MSIX with 17,973 entries including the manifest, self-contained Desktop, bundled Node, Sidecar, and sample RAG server. SHA-256 self-signed signature creation and SignTool verification pass.

## Post-phase stabilization

- MCP reconfiguration and health failure now retire a session only after its active calls finish. Calls on different connections remain concurrent, while final shutdown waits for active calls within the existing process grace period.
- MCP metadata and its Credential Manager secret are saved as one rollback-protected operation. Switching to stdio or unauthenticated HTTP deletes a stale Bearer credential instead of retaining it.
- Diagnostic bundles are written to a same-directory temporary file and atomically replace the destination only after a valid archive is complete. Cancellation preserves an existing bundle and removes the temporary file. JSON credentials and `Authorization: Bearer` values are redacted in addition to the existing patterns.
- Returning to a cached Settings page restores controls after a cancelled operation. Superseded or unloaded Memory/Schedule page refreshes now cancel remaining SQLite pagination instead of consuming resources in the background.
- UI smoke waits for the exact Desktop PID it started to exit before returning, preventing false failures in sequential smoke/soak automation.
- Latest verification: Sidecar 50, Contracts 7, Application 18, Desktop 174, and Named Pipe integration 1 all pass with zero build warnings/errors. A five-restart follow-up soak measured 21,094,400 bytes of private-memory growth and 80 handles, within the 128 MiB/256 acceptance limits.

## External release gates

- A production-trusted code-signing identity and timestamp service are external release inputs, not repository secrets.
- Install→startup-login→App Installer update→remove must run once in a disposable clean Windows standard-user account using a machine-trusted signing chain. The current non-administrator workstation correctly rejects its temporary CurrentUser self-signed root with `0x800B0109`; no certificate or package remains installed after the test.
- Windows 10 22H2 and representative third-party UIA application acceptance remain hardware/OS matrix activities and do not change the implemented safety boundary.
