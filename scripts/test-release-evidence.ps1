[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("LIGClaw-release-evidence-" + [guid]::NewGuid().ToString('N'))
try {
    $path = Join-Path $temporaryRoot 'release-evidence.json'
    $checks = @(
        [ordered]@{ name = 'mcp-cycle'; status = 'passed'; iterations = 20 },
        [ordered]@{ name = 'desktop-sidecar-restart'; status = 'passed'; iterations = 10 },
        [ordered]@{ name = 'persistence-recovery'; status = 'passed' },
        [ordered]@{ name = 'diagnostic-redaction'; status = 'passed' },
        [ordered]@{ name = 'hardware-sleep-resume'; status = 'manual-required' }
    )
    $writtenPath = & (Join-Path $PSScriptRoot 'write-release-evidence.ps1') `
        -OutputPath $path -Revision 'fixture-revision' -TrackedChangesPresent $false -Checks $checks
    $document = Get-Content -Raw -LiteralPath $writtenPath | ConvertFrom-Json
    if ($document.schemaVersion -ne 1 -or $document.automatedStatus -ne 'passed' -or
        $document.checks.Count -ne 5 -or $document.trackedChangesPresent) {
        throw 'Release evidence fields did not match their authoritative inputs.'
    }
    if ($document.PSObject.Properties.Name -contains 'userProfile' -or
        $document.PSObject.Properties.Name -contains 'commandOutput') {
        throw 'Release evidence must not contain user paths or command output.'
    }
    Write-Output 'Release evidence fixture test passed.'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
