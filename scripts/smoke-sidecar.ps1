[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$desktopPath = (Resolve-Path (Join-Path $repositoryRoot 'src/LIGClaw.Desktop/bin/Debug/net10.0-windows/LIGClaw.Desktop.exe')).Path
$desktop = Start-Process -FilePath $desktopPath -WindowStyle Hidden -PassThru
$observedSidecars = [System.Collections.Generic.List[int]]::new()
$failure = $null

function Wait-Sidecar {
    param(
        [int]$ParentPid,
        [int]$ExcludedPid = 0,
        [int]$TimeoutSeconds = 12
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 250
        $candidate = Get-CimInstance Win32_Process -Filter "ParentProcessId = $ParentPid" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq 'node.exe' -and $_.ProcessId -ne $ExcludedPid } |
            Select-Object -First 1
    } while (-not $candidate -and [DateTime]::UtcNow -lt $deadline)

    if (-not $candidate) {
        throw 'Timed out waiting for the Sidecar process.'
    }
    return [int]$candidate.ProcessId
}

try {
    $firstPid = Wait-Sidecar -ParentPid $desktop.Id
    $observedSidecars.Add($firstPid)

    $firstProcess = Get-CimInstance Win32_Process -Filter "ProcessId = $firstPid"
    if ($firstProcess.CommandLine -match '(?i)--token|LIGCLAW_SESSION_TOKEN') {
        throw 'Session token material is exposed in the Sidecar command line.'
    }

    Start-Sleep -Seconds 5
    if (-not (Get-Process -Id $firstPid -ErrorAction SilentlyContinue)) {
        throw 'Sidecar did not survive two heartbeat intervals.'
    }

    Stop-Process -Id $firstPid -Force
    $secondPid = Wait-Sidecar -ParentPid $desktop.Id -ExcludedPid $firstPid
    $observedSidecars.Add($secondPid)

    if ($desktop.HasExited) {
        throw "Desktop exited unexpectedly with code $($desktop.ExitCode)."
    }
}
catch {
    $failure = $_
}
finally {
    if (-not $desktop.HasExited) {
        Stop-Process -Id $desktop.Id -Force
    }
    $desktop.WaitForExit()

    foreach ($sidecarPid in $observedSidecars) {
        $deadline = [DateTime]::UtcNow.AddSeconds(8)
        do {
            Start-Sleep -Milliseconds 250
            $orphan = Get-Process -Id $sidecarPid -ErrorAction SilentlyContinue
        } while ($orphan -and [DateTime]::UtcNow -lt $deadline)

        if ($orphan) {
            Stop-Process -Id $sidecarPid -Force
            if (-not $failure) {
                $failure = [System.Management.Automation.ErrorRecord]::new(
                    [InvalidOperationException]::new('Sidecar remained after Desktop termination.'),
                    'OrphanedSidecar',
                    [System.Management.Automation.ErrorCategory]::ResourceBusy,
                    $sidecarPid)
            }
        }
    }
}

if ($failure) {
    throw $failure
}

Write-Output 'Sidecar handshake, heartbeat, restart, and cleanup smoke test passed.'
exit 0
