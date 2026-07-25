# ADR 0006: Select Windows behavior by runtime capability

## Status

Accepted for Phase 1 on 2026-07-25.

## Decision

- Support Windows client 10 version 1809 (build 17763) and later. Build 22000 is classified as Windows 11 or later; later builds are not rejected merely because they are unknown.
- Read the real OS build with `RtlGetVersion` and convert it once into a `WindowsPlatformProfile` owned by Desktop.
- Share Win32 implementations when Windows 10 and 11 expose the same contract. A Tool must branch only when its required API or behavior actually differs.
- Represent availability as named capabilities. Future Tool adapters declare capabilities and are selected by the Desktop host instead of reading OS versions throughout Tool code.
- Guard OS-specific calls at runtime. The initial Windows 11 adapter applies the Windows 11 DWM corner preference; Windows 10 keeps the normal WPF window path.
- Set the Desktop target and supported platform baseline to `windows10.0.17763.0`.

## Consequences

- Windows 10 does not receive calls to Windows 11-only DWM attributes.
- Common shell, Credential Manager, global hotkey, process, and window enumeration code stays shared and easier to test.
- Unit tests simulate Windows 10, Windows 11, future builds, old builds, and server products without depending on the development PC.
- Release acceptance still requires an actual Windows 10 VM/device run because build classification tests cannot prove native API deployment and rendering by themselves.
- When an API can be redistributed or has its own availability probe, the adapter must check that API directly in addition to the OS capability.
