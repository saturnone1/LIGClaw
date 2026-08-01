[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("LIGClaw-release-manifest-" + [guid]::NewGuid().ToString('N'))
try {
    $stage = Join-Path $temporaryRoot 'stage'
    $sidecar = Join-Path $stage 'sidecar'
    New-Item -ItemType Directory -Path $sidecar -Force | Out-Null
    $package = Join-Path $temporaryRoot 'LIGClaw-9.8.7.6-x64.msix'
    $contents = Join-Path $temporaryRoot 'package-contents.txt'
    [System.IO.File]::WriteAllBytes($package, [byte[]](1, 2, 3, 4))
    [System.IO.File]::WriteAllBytes((Join-Path $sidecar 'node.exe'), [byte[]](77, 90))
    Set-Content -LiteralPath $contents -Value "sidecar\node.exe`t2" -Encoding ascii
    $manifestPath = & (Join-Path $PSScriptRoot 'write-release-manifest.ps1') `
        -Package $package -Stage $stage -ContentsPath $contents -OutputRoot $temporaryRoot `
        -Version '9.8.7.6' -Publisher 'CN=Test' -Signed $false -VerificationStatus passed
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.version -ne '9.8.7.6' -or $manifest.protocolVersion -ne '1.13' -or
        $manifest.databaseSchemaVersion -ne 11 -or $manifest.packageBytes -ne 4 -or
        $manifest.verification.status -ne 'passed') {
        throw 'Release manifest fields did not match their authoritative inputs.'
    }
    $checksums = Get-Content -LiteralPath (Join-Path $temporaryRoot 'SHA256SUMS.txt')
    if ($checksums.Count -ne 3) { throw 'Release checksum inventory is incomplete.' }
    Write-Output 'Release manifest evidence test passed.'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
