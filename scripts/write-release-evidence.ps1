[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(Mandatory = $true)][string]$Revision,
    [Parameter(Mandatory = $true)][bool]$TrackedChangesPresent,
    [Parameter(Mandatory = $true)][System.Collections.IDictionary[]]$Checks
)

$ErrorActionPreference = 'Stop'
$requiredChecks = @(
    'mcp-cycle',
    'desktop-sidecar-restart',
    'persistence-recovery',
    'diagnostic-redaction',
    'hardware-sleep-resume'
)
$allowedStatuses = @('passed', 'failed', 'manual-required')
$names = @($Checks | ForEach-Object { [string]$_['name'] })
foreach ($name in $requiredChecks) {
    if (@($names | Where-Object { $_ -eq $name }).Count -ne 1) {
        throw "Release evidence requires exactly one '$name' check."
    }
}
foreach ($check in $Checks) {
    if ([string]$check['status'] -notin $allowedStatuses) {
        throw "Unsupported release evidence status: $($check['status'])"
    }
}

$fullPath = [System.IO.Path]::GetFullPath($OutputPath)
$directory = Split-Path -Parent $fullPath
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$temporaryPath = "$fullPath.$([guid]::NewGuid().ToString('N')).tmp"
$document = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    revision = $Revision
    trackedChangesPresent = $TrackedChangesPresent
    automatedStatus = if (@($Checks | Where-Object { $_['status'] -eq 'failed' }).Count -eq 0) { 'passed' } else { 'failed' }
    checks = $Checks
}
try {
    $json = $document | ConvertTo-Json -Depth 6
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($temporaryPath, $json, $encoding)
    Move-Item -LiteralPath $temporaryPath -Destination $fullPath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
}

Write-Output $fullPath
