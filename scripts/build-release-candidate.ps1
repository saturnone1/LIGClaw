[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version = '0.6.0.0',
    [string]$Publisher = 'CN=LIGClaw Development',
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$CertificateThumbprint,
    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string]$CertificateStoreLocation = 'CurrentUser',
    [uri]$TimestampUrl,
    [uri]$DistributionBaseUri
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ($CertificateThumbprint -and -not $TimestampUrl) {
    throw '서명된 release candidate에는 신뢰된 HTTPS TimestampUrl이 필요합니다.'
}
if ($TimestampUrl -and -not $CertificateThumbprint) {
    throw 'TimestampUrl은 CertificateThumbprint와 함께 사용해야 합니다.'
}
$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10\bin'
if (-not (Test-Path -LiteralPath $sdkRoot -PathType Container)) {
    throw "Windows 10/11 SDK packaging tools are required: $sdkRoot"
}

$evidencePath = Join-Path $repositoryRoot "artifacts\release-evidence\release-evidence-$Version.json"
& (Join-Path $PSScriptRoot 'collect-release-evidence.ps1') -OutputPath $evidencePath
if ($LASTEXITCODE -ne 0) { throw 'Release evidence collection failed.' }

& (Join-Path $PSScriptRoot 'test-powershell51-parse.ps1')
if ($LASTEXITCODE -ne 0) { throw 'PowerShell compatibility check failed.' }
& (Join-Path $PSScriptRoot 'verify.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Repository verification failed.' }

$arguments = @{
    Version = $Version
    Publisher = $Publisher
    VerificationStatus = 'passed'
    EvidencePath = $evidencePath
}
if ($CertificateThumbprint) {
    $arguments.CertificateThumbprint = $CertificateThumbprint
    $arguments.CertificateStoreLocation = $CertificateStoreLocation
}
if ($TimestampUrl) { $arguments.TimestampUrl = $TimestampUrl }
if ($DistributionBaseUri) { $arguments.DistributionBaseUri = $DistributionBaseUri }
& (Join-Path $PSScriptRoot 'build-msix.ps1') @arguments
