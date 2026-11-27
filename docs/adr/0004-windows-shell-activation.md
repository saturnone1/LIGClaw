# ADR 0004: Keep shell activation local to the Windows user session

## Status

Accepted for Phase 1 on 2026-07-25.

## Decision

- Enforce one LIGClaw Desktop process per Windows logon session with `Local\` named mutex and auto-reset event objects.
- A second launch performs no work itself; it signals the primary process to restore and focus its request input.
- Register the user-selected quick-access shortcut with `RegisterHotKey` only while the primary process is alive. The default is `Ctrl + Alt + Space`; Settings offers bounded alternative combinations and a failed change restores the previous working shortcut.
- Store the explicit “Windows 시작 시 자동 실행” choice in the current user's standard `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` key.
- Startup registration launches the packaged executable with `--background`, creating the tray and runtime without opening a window.

## Consequences

- No administrator right, service, scheduled task, machine-wide registry value, or cross-session IPC is required.
- The startup entry is created only after an explicit user save and can be removed from the same screen.
- Named object activation carries no prompt, file content, credential, or command payload; it can only request that the existing window become visible.
- A quick-access shortcut can conflict with another application. LIGClaw keeps the user's explicit choice, explains the conflict, offers bounded alternatives in Settings, and remains usable through its tray icon and normal launch while registration is unavailable.
