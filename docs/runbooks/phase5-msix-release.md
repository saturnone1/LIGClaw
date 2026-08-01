# Phase 5 MSIX release and acceptance runbook

## Build and sign

Install the Windows 10/11 SDK packaging tools (`MakeAppx.exe` and `SignTool.exe`) first. A reproducible unsigned release candidate, including repository verification, is produced with one command:

```powershell
./scripts/build-release-candidate.ps1 -Version 1.0.0.0
```

The command first collects automated release evidence, then runs the full repository verification and packaging. Close any user-running LIGClaw instance before starting because the Desktop/Sidecar restart soak intentionally refuses to terminate it. The output folder contains the MSIX, `package-contents.txt`, `release-evidence.json`, `release-manifest.json`, and `SHA256SUMS.txt`. The manifest records the package hash and size, bundled Node version, protocol version, Desktop database schema version, signing state, staged file count, verification result, and the evidence hash. Failed builds remove their incomplete version output so the same command can be retried.

The evidence pass can be run independently on a machine without the Windows SDK:

```powershell
./scripts/collect-release-evidence.ps1
```

It runs 20 MCP cycles, 10 Desktop-owned Sidecar restarts, deterministic database backup/restore and resume reconciliation tests, and diagnostic-bundle redaction tests. The JSON stores only bounded status, duration, iteration count, revision, and tracked-worktree state; it never embeds command output or user paths. Physical sleep/resume remains `manual-required` and must be recorded during the Windows 10/11 acceptance pass.

Production packages require a trusted code-signing certificate whose subject exactly matches the manifest publisher. Build and sign with SHA-256 and a trusted timestamp:

```powershell
./scripts/build-msix.ps1 -Version 1.0.0.0 `
  -Publisher "CN=<production publisher>" `
  -CertificateThumbprint <thumbprint> `
  -TimestampUrl https://<trusted-rfc3161-service> `
  -DistributionBaseUri https://<release-host>/ligclaw
```

The output contains a self-contained .NET Desktop, the locked production Sidecar dependencies, and a bundled Node runtime. End-user PATH is not used. `LIGClaw.appinstaller` uses the 2021 schema, checks on launch, permits background checks, and does not block activation while downloading.

Do not distribute the development certificate produced by `test-msix-development.ps1`. Production signing should use a CA/enterprise certificate or Azure Artifact Signing. Keep private keys and timestamp credentials out of the repository and logs.

## Clean-user acceptance

Use a disposable Windows 10 22H2 or Windows 11 VM and a standard local user. The signing chain must already be trusted by the machine. From a normal PowerShell prompt:

```powershell
./scripts/test-msix-lifecycle.ps1 `
  -InitialPackage .\LIGClaw-1.0.0.0-x64.msix `
  -UpdatePackage .\LIGClaw-1.0.0.1-x64.msix
```

The script refuses to replace an existing LIGClaw installation, then verifies install, shell launch, upgrade to a strictly higher version, and removal. It always attempts removal in `finally`.

Manually verify once per release candidate:

1. Install through the `.appinstaller` file and launch LIGClaw.
2. In Settings, enable Windows startup. Sign out and back in; confirm the app starts in the tray without showing the main window.
3. Publish the higher package and updated `.appinstaller`; relaunch and confirm the update policy applies.
4. Confirm model and MCP tokens remain in Windows Credential Manager and never appear in the diagnostic bundle.
5. Uninstall from Windows Settings and confirm package files/startup registration are removed. Local-first conversation data under `%LOCALAPPDATA%\LIGClaw` is user data and is intentionally retained unless the user explicitly deletes it.

Microsoft requires installed MSIX packages to be signed by a chain trusted on the device. A non-administrator development shell cannot establish machine trust for an executable test package; run the lifecycle test in the clean VM with its test root pre-provisioned or use a production-trusted signature.

References: [MSIX signing](https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview), [App Installer update and repair](https://learn.microsoft.com/en-us/windows/msix/app-installer/auto-update-and-repair--overview), [MSIX PowerShell management](https://learn.microsoft.com/en-ie/windows/msix/desktop/powershell-msix-cmdlets).
