[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version = '0.6.0.0',
    [string]$Publisher = 'CN=LIGClaw Development',
    [string]$CertificateThumbprint,
    [string]$TimestampUrl,
    [uri]$DistributionBaseUri
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
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
if ($CertificateThumbprint) { $arguments.CertificateThumbprint = $CertificateThumbprint }
if ($TimestampUrl) { $arguments.TimestampUrl = $TimestampUrl }
if ($DistributionBaseUri) { $arguments.DistributionBaseUri = $DistributionBaseUri }
& (Join-Path $PSScriptRoot 'build-msix.ps1') @arguments
