[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InitialPackage,
    [Parameter(Mandatory)][string]$StageDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$UpdateVersion = '0.5.0.4'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Set-Utf8NoBom {
    param([string]$LiteralPath, [string]$Value)
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($LiteralPath, $Value, $encoding)
}
$initial = (Resolve-Path -LiteralPath $InitialPackage).Path
$stage = (Resolve-Path -LiteralPath $StageDirectory).Path
$allowedStageRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\msix')) + [System.IO.Path]::DirectorySeparatorChar
if (-not $stage.StartsWith($allowedStageRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Stage directory must be under artifacts/msix.' }
$outputDirectory = Split-Path -Parent $initial
$update = Join-Path $outputDirectory "LIGClaw-$UpdateVersion-x64.msix"
$reuseUpdate = Test-Path -LiteralPath $update

$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10\bin'
$makeAppx = Get-ChildItem $sdkRoot -Recurse -Filter makeappx.exe | Where-Object FullName -Match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1
$signTool = Get-ChildItem $sdkRoot -Recurse -Filter signtool.exe | Where-Object FullName -Match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeAppx -or -not $signTool) { throw 'Windows SDK packaging tools were not found.' }

$certificate = $null
try {
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=LIGClaw Development' `
        -KeyUsage DigitalSignature -FriendlyName 'LIGClaw temporary MSIX lifecycle test' `
        -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddDays(2)
    $trusted = New-Object System.Security.Cryptography.X509Certificates.X509Store('TrustedPeople', 'CurrentUser')
    try { $trusted.Open('ReadWrite'); $trusted.Add($certificate) } finally { $trusted.Close() }
    $root = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'CurrentUser')
    try { $root.Open('ReadWrite'); $root.Add($certificate) } finally { $root.Close() }

    & $signTool.FullName sign /fd SHA256 /sha1 $certificate.Thumbprint $initial
    if ($LASTEXITCODE -ne 0) { throw 'Initial package signing failed.' }

    if (-not $reuseUpdate) {
        $manifestPath = Join-Path $stage 'AppxManifest.xml'
        $manifest = Get-Content -Raw -LiteralPath $manifestPath
        $manifest = [regex]::Replace($manifest, 'Version="\d+\.\d+\.\d+\.\d+"', "Version=`"$UpdateVersion`"", 1)
        Set-Utf8NoBom -LiteralPath $manifestPath -Value $manifest
        & $makeAppx.FullName pack /d $stage /p $update
        if ($LASTEXITCODE -ne 0) { throw 'Update package creation failed.' }
    }
    & $signTool.FullName sign /fd SHA256 /sha1 $certificate.Thumbprint $update
    if ($LASTEXITCODE -ne 0) { throw 'Update package signing failed.' }
    & $signTool.FullName verify /pa $initial
    if ($LASTEXITCODE -ne 0) { throw 'Initial signature verification failed.' }
    & $signTool.FullName verify /pa $update
    if ($LASTEXITCODE -ne 0) { throw 'Update signature verification failed.' }

    & (Join-Path $PSScriptRoot 'test-msix-lifecycle.ps1') -InitialPackage $initial -UpdatePackage $update
    if ($LASTEXITCODE -ne 0) { throw 'MSIX lifecycle test failed.' }
}
finally {
    if ($certificate) {
        $trusted = New-Object System.Security.Cryptography.X509Certificates.X509Store('TrustedPeople', 'CurrentUser')
        try { $trusted.Open('ReadWrite'); $trusted.Remove($certificate) } finally { $trusted.Close() }
        $root = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'CurrentUser')
        try { $root.Open('ReadWrite'); $root.Remove($certificate) } finally { $root.Close() }
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -Force -ErrorAction SilentlyContinue
    }
}
