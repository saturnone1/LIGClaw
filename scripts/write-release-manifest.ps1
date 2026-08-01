[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Package,
    [Parameter(Mandatory = $true)][string]$Stage,
    [Parameter(Mandatory = $true)][string]$ContentsPath,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Publisher,
    [Parameter(Mandatory = $true)][bool]$Signed,
    [ValidateSet('passed', 'not-recorded')][string]$VerificationStatus = 'not-recorded',
    [string]$AppInstallerPath,
    [string]$EvidencePath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
foreach ($path in @($Package, $Stage, $ContentsPath, $OutputRoot)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Release evidence input is missing: $path" }
}
$protocol = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'contracts\protocol.json') | ConvertFrom-Json
$databaseSource = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'src\LIGClaw.Desktop\Infrastructure\Persistence\ConversationDatabase.cs')
$schemaMatch = [regex]::Match($databaseSource, 'CurrentSchemaVersion\s*=\s*(\d+)')
if (-not $schemaMatch.Success) { throw 'Could not resolve the Desktop database schema version.' }
$nodePath = Join-Path $Stage 'sidecar\node.exe'
if (-not (Test-Path -LiteralPath $nodePath -PathType Leaf)) { throw 'Bundled Node executable is missing.' }
$releaseManifest = [ordered]@{
    product = 'LIGClaw'
    version = $Version
    architecture = 'x64'
    publisher = $Publisher
    protocolVersion = [string]$protocol.protocolVersion
    databaseSchemaVersion = [int]$schemaMatch.Groups[1].Value
    package = Split-Path -Leaf $Package
    packageBytes = (Get-Item -LiteralPath $Package).Length
    packageSha256 = (Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash.ToLowerInvariant()
    signed = $Signed
    bundledNodeVersion = (Get-Item -LiteralPath $nodePath).VersionInfo.FileVersion
    stagedFileCount = @(Get-ChildItem -LiteralPath $Stage -File -Recurse).Count
    verification = [ordered]@{
        status = $VerificationStatus
        command = './scripts/verify.ps1'
    }
}
if ($EvidencePath) {
    if (-not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) {
        throw "Release evidence file is missing: $EvidencePath"
    }
    $evidence = Get-Content -Raw -LiteralPath $EvidencePath | ConvertFrom-Json
    if ($evidence.automatedStatus -ne 'passed') {
        throw 'Release evidence automated checks have not passed.'
    }
    $releaseManifest.releaseEvidence = [ordered]@{
        file = Split-Path -Leaf $EvidencePath
        sha256 = (Get-FileHash -LiteralPath $EvidencePath -Algorithm SHA256).Hash.ToLowerInvariant()
        automatedStatus = [string]$evidence.automatedStatus
        hardwareSleepResume = [string]($evidence.checks | Where-Object { $_.name -eq 'hardware-sleep-resume' } | Select-Object -ExpandProperty status)
    }
}
$releaseManifestPath = Join-Path $OutputRoot 'release-manifest.json'
$releaseManifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $releaseManifestPath -Encoding utf8
$checksumFiles = @($Package, $ContentsPath, $releaseManifestPath)
if ($EvidencePath) { $checksumFiles += $EvidencePath }
if ($AppInstallerPath -and (Test-Path -LiteralPath $AppInstallerPath -PathType Leaf)) {
    $checksumFiles += $AppInstallerPath
}
$checksumFiles | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $_)"
} | Set-Content -LiteralPath (Join-Path $OutputRoot 'SHA256SUMS.txt') -Encoding ascii
Write-Output $releaseManifestPath
