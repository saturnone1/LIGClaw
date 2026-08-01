[CmdletBinding()]
param(
    [string]$OutputPath,
    [ValidateRange(1, 200)][int]$McpIterations = 20,
    [ValidateRange(1, 100)][int]$DesktopRestarts = 10
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputPath) {
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $OutputPath = Join-Path $repositoryRoot "artifacts\release-evidence\release-evidence-$stamp.json"
}
$checks = New-Object System.Collections.Generic.List[System.Collections.IDictionary]

function Invoke-EvidenceCheck {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [hashtable]$Metadata = @{}
    )

    $started = [DateTimeOffset]::UtcNow
    $status = 'passed'
    try {
        & $Action | Out-Host
    }
    catch {
        $status = 'failed'
        Write-Warning "$Name failed: $($_.Exception.Message)"
    }
    $result = [ordered]@{
        name = $Name
        status = $status
        durationSeconds = [Math]::Round(([DateTimeOffset]::UtcNow - $started).TotalSeconds, 3)
    }
    foreach ($key in $Metadata.Keys) { $result[$key] = $Metadata[$key] }
    $checks.Add($result)
}

Invoke-EvidenceCheck -Name 'mcp-cycle' -Metadata @{ iterations = $McpIterations } -Action {
    & (Join-Path $PSScriptRoot 'soak-mcp.ps1') -Iterations $McpIterations
}
Invoke-EvidenceCheck -Name 'desktop-sidecar-restart' -Metadata @{ iterations = $DesktopRestarts } -Action {
    & (Join-Path $PSScriptRoot 'soak-desktop.ps1') -Restarts $DesktopRestarts
}
Invoke-EvidenceCheck -Name 'persistence-recovery' -Action {
    dotnet test (Join-Path $repositoryRoot 'tests\LIGClaw.Desktop.Tests\LIGClaw.Desktop.Tests.csproj') `
        --configuration Debug --artifacts-path (Join-Path $repositoryRoot 'artifacts\release-evidence\test-output') `
        --filter 'FullyQualifiedName~DataMaintenanceTests|FullyQualifiedName~RuntimeReconciliationAppliesEveryMisfirePolicyAfterResume'
    if ($LASTEXITCODE -ne 0) { throw "Persistence recovery tests exited with code $LASTEXITCODE." }
}
Invoke-EvidenceCheck -Name 'diagnostic-redaction' -Action {
    dotnet test (Join-Path $repositoryRoot 'tests\LIGClaw.Desktop.Tests\LIGClaw.Desktop.Tests.csproj') `
        --configuration Debug --artifacts-path (Join-Path $repositoryRoot 'artifacts\release-evidence\test-output') `
        --filter 'FullyQualifiedName~DiagnosticBundleServiceTests'
    if ($LASTEXITCODE -ne 0) { throw "Diagnostic redaction tests exited with code $LASTEXITCODE." }
}
$checks.Add([ordered]@{
    name = 'hardware-sleep-resume'
    status = 'manual-required'
    reason = 'Physical sleep and resume remains an external Windows 10/11 acceptance check.'
})

$revision = (git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the repository revision.' }
$trackedStatus = @(git -C $repositoryRoot status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the tracked worktree status.' }
$writtenPath = & (Join-Path $PSScriptRoot 'write-release-evidence.ps1') `
    -OutputPath $OutputPath `
    -Revision $revision `
    -TrackedChangesPresent ($trackedStatus.Count -gt 0) `
    -Checks $checks.ToArray()
Write-Output "Release evidence: $writtenPath"

if (@($checks | Where-Object { $_['status'] -eq 'failed' }).Count -gt 0) {
    throw 'One or more automated release evidence checks failed.'
}
