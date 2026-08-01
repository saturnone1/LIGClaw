[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$verifyArtifacts = Join-Path $repositoryRoot 'artifacts\verify'

Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File | ForEach-Object {
    $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
    $hasUtf8Bom = $bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF
    $hasNonAscii = $null -ne ($bytes | Where-Object { $_ -gt 0x7F } | Select-Object -First 1)
    if ($hasNonAscii -and -not $hasUtf8Bom) {
        throw "PowerShell 5.1 requires a UTF-8 BOM for non-ASCII script: $($_.Name)"
    }
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath exited with code $LASTEXITCODE."
    }
}

Invoke-Checked -FilePath powershell.exe -ArgumentList @(
    '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
    (Join-Path $PSScriptRoot 'test-powershell51-parse.ps1'))
Invoke-Checked -FilePath powershell.exe -ArgumentList @(
    '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
    (Join-Path $PSScriptRoot 'test-release-manifest.ps1'))

Push-Location (Join-Path $repositoryRoot 'sidecar')
try {
    Invoke-Checked npm ci
    Invoke-Checked npm run check:contracts
    Invoke-Checked npm run check
    Invoke-Checked npm run build
    Invoke-Checked npm test
    Invoke-Checked npm audit --audit-level=moderate
}
finally {
    Pop-Location
}

Push-Location $repositoryRoot
try {
    Invoke-Checked dotnet format LIGClaw.slnx --verify-no-changes --no-restore
    Invoke-Checked dotnet build LIGClaw.slnx --configuration Debug --artifacts-path $verifyArtifacts
    Invoke-Checked dotnet test LIGClaw.slnx --configuration Debug --no-build --artifacts-path $verifyArtifacts
}
finally {
    Pop-Location
}
