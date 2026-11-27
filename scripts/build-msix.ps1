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
    [uri]$DistributionBaseUri,
    [ValidateSet('passed', 'not-recorded')]
    [string]$VerificationStatus = 'not-recorded',
    [string]$EvidencePath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $repositoryRoot "artifacts\msix\$Version"
$signingCertificate = $null
if ($TimestampUrl -and -not $CertificateThumbprint) {
    throw 'TimestampUrl은 CertificateThumbprint와 함께 사용해야 합니다.'
}
if ($TimestampUrl -and $TimestampUrl.Scheme -ne 'https') {
    throw 'TimestampUrl은 HTTPS 주소여야 합니다.'
}
if ($CertificateThumbprint) {
    $normalizedThumbprint = $CertificateThumbprint.ToUpperInvariant()
    $certificatePath = "Cert:\$CertificateStoreLocation\My\$normalizedThumbprint"
    $signingCertificate = Get-Item -LiteralPath $certificatePath -ErrorAction Stop
    if (-not $signingCertificate.HasPrivateKey) {
        throw 'MSIX 서명 인증서에 접근 가능한 private key가 없습니다.'
    }
    $now = Get-Date
    if ($signingCertificate.NotBefore -gt $now -or $signingCertificate.NotAfter -le $now) {
        throw 'MSIX 서명 인증서가 아직 유효하지 않거나 만료되었습니다.'
    }
    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    $hasCodeSigningUsage = $null -ne ($signingCertificate.EnhancedKeyUsageList | Where-Object {
        $objectId = $_.ObjectId
        $value = if ($objectId -is [System.Security.Cryptography.Oid]) {
            $objectId.Value
        }
        else {
            [string]$objectId
        }
        $value -eq $codeSigningOid
    } | Select-Object -First 1)
    if (-not $hasCodeSigningUsage) {
        throw 'MSIX 서명 인증서는 Code Signing EKU를 포함해야 합니다.'
    }
    $certificateSubject = $signingCertificate.SubjectName.Name.Trim()
    if (-not [string]::Equals($Publisher.Trim(), $certificateSubject, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'manifest Publisher가 서명 인증서 subject와 일치하지 않습니다.'
    }
    $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
    $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
    if (-not $chain.Build($signingCertificate)) {
        throw 'MSIX signing certificate chain is not trusted on this computer.'
    }
}
$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10\bin'
if (-not (Test-Path -LiteralPath $sdkRoot -PathType Container)) {
    throw "Windows 10/11 SDK packaging tools are required: $sdkRoot"
}
$stage = Join-Path $outputRoot 'stage'
$package = Join-Path $outputRoot "LIGClaw-$Version-x64.msix"
if (Test-Path -LiteralPath $outputRoot) { throw "출력 폴더가 이미 있습니다: $outputRoot" }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
trap {
    if (Test-Path -LiteralPath $outputRoot) {
        Remove-Item -LiteralPath $outputRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    break
}

function Set-Utf8NoBom {
    param([string]$LiteralPath, [string]$Value)
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($LiteralPath, $Value, $encoding)
}

$packagedEvidencePath = $null
if ($EvidencePath) {
    if (-not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) {
        throw "Release evidence file is missing: $EvidencePath"
    }
    $evidence = Get-Content -Raw -LiteralPath $EvidencePath | ConvertFrom-Json
    if ($evidence.automatedStatus -ne 'passed') {
        throw 'Release evidence automated checks have not passed.'
    }
    $packagedEvidencePath = Join-Path $outputRoot 'release-evidence.json'
    Copy-Item -LiteralPath $EvidencePath -Destination $packagedEvidencePath
}

npm ci --prefix (Join-Path $repositoryRoot 'sidecar')
if ($LASTEXITCODE -ne 0) { throw 'Sidecar dependency restore failed.' }
npm run build --prefix (Join-Path $repositoryRoot 'sidecar')
if ($LASTEXITCODE -ne 0) { throw 'Sidecar build failed.' }

dotnet publish (Join-Path $repositoryRoot 'src\LIGClaw.Desktop\LIGClaw.Desktop.csproj') `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $stage
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }

$sidecarStage = Join-Path $stage 'sidecar'
New-Item -ItemType Directory -Path $sidecarStage -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'sidecar\dist') -Destination $sidecarStage -Recurse
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'sidecar\package.json') -Destination $sidecarStage
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'sidecar\package-lock.json') -Destination $sidecarStage
npm ci --omit=dev --prefix $sidecarStage
if ($LASTEXITCODE -ne 0) { throw 'Packaged Sidecar dependency restore failed.' }
$node = (Get-Command node -ErrorAction Stop).Source
Copy-Item -LiteralPath $node -Destination (Join-Path $sidecarStage 'node.exe')

$requiredStageFiles = @(
    'LIGClaw.Desktop.exe',
    'LIGClaw.Desktop.dll',
    'Wpf.Ui.dll',
    'ThirdPartyNotices.txt',
    'sidecar\node.exe',
    'sidecar\dist\index.js',
    'sidecar\package.json',
    'sidecar\package-lock.json'
)
foreach ($relativePath in $requiredStageFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $stage $relativePath) -PathType Leaf)) {
        throw "Required package file is missing: $relativePath"
    }
}

$assets = Join-Path $stage 'Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
Add-Type -AssemblyName System.Drawing
$icon = [System.Drawing.Icon]::ExtractAssociatedIcon((Join-Path $stage 'LIGClaw.Desktop.exe'))
foreach ($entry in @(@('StoreLogo.png', 50), @('Square44x44Logo.png', 44), @('Square150x150Logo.png', 150))) {
    $bitmap = New-Object System.Drawing.Bitmap($entry[1], $entry[1])
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $rectangle = [System.Drawing.Rectangle]::new(0, 0, $entry[1], $entry[1])
        $graphics.DrawIcon($icon, $rectangle)
        $bitmap.Save((Join-Path $assets $entry[0]), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
}
$icon.Dispose()

$manifest = Get-Content -Raw (Join-Path $repositoryRoot 'packaging\AppxManifest.xml.template')
$manifest = $manifest.Replace('__VERSION__', $Version).Replace('__PUBLISHER__', $Publisher)
Set-Utf8NoBom -LiteralPath (Join-Path $stage 'AppxManifest.xml') -Value $manifest
[xml]$manifestXml = $manifest
$identity = $manifestXml.Package.Identity
if ($identity.Version -ne $Version -or $identity.Publisher -ne $Publisher -or $identity.ProcessorArchitecture -ne 'x64') {
    throw 'Generated AppxManifest identity does not match the requested release.'
}

$contentsPath = Join-Path $outputRoot 'package-contents.txt'
$stagePrefixLength = $stage.TrimEnd('\').Length + 1
Get-ChildItem -LiteralPath $stage -File -Recurse |
    Sort-Object FullName |
    ForEach-Object { "$($_.FullName.Substring($stagePrefixLength))`t$($_.Length)" } |
    Set-Content -LiteralPath $contentsPath -Encoding utf8

$makeAppx = Get-ChildItem $sdkRoot -Recurse -Filter makeappx.exe | Where-Object FullName -Match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeAppx) { throw 'Windows SDK MakeAppx.exe를 찾지 못했습니다.' }
& $makeAppx.FullName pack /d $stage /p $package
if ($LASTEXITCODE -ne 0) { throw 'MSIX packaging failed.' }

if ($CertificateThumbprint) {
    $signTool = Get-ChildItem $sdkRoot -Recurse -Filter signtool.exe | Where-Object FullName -Match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signTool) { throw 'Windows SDK SignTool.exe를 찾지 못했습니다.' }
    $arguments = @('sign', '/fd', 'SHA256', '/sha1', $signingCertificate.Thumbprint)
    if ($CertificateStoreLocation -eq 'LocalMachine') { $arguments += '/sm' }
    if ($TimestampUrl) { $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256') }
    $arguments += $package
    & $signTool.FullName @arguments
    if ($LASTEXITCODE -ne 0) { throw 'MSIX signing failed.' }
    & $signTool.FullName verify /pa $package
    if ($LASTEXITCODE -ne 0) { throw 'MSIX signature verification failed.' }
}

if ($DistributionBaseUri) {
    $base = $DistributionBaseUri.AbsoluteUri.TrimEnd('/')
    $appInstaller = Get-Content -Raw (Join-Path $repositoryRoot 'packaging\LIGClaw.appinstaller.template')
    $appInstaller = $appInstaller.Replace('__VERSION__', $Version)
    $appInstaller = $appInstaller.Replace('__PUBLISHER__', $Publisher)
    $appInstaller = $appInstaller.Replace('__APPINSTALLER_URI__', "$base/LIGClaw.appinstaller")
    $appInstaller = $appInstaller.Replace('__PACKAGE_URI__', "$base/$(Split-Path -Leaf $package)")
    Set-Utf8NoBom -LiteralPath (Join-Path $outputRoot 'LIGClaw.appinstaller') -Value $appInstaller
}

$appInstallerPath = Join-Path $outputRoot 'LIGClaw.appinstaller'
$releaseManifestPath = & (Join-Path $PSScriptRoot 'write-release-manifest.ps1') `
    -Package $package `
    -Stage $stage `
    -ContentsPath $contentsPath `
    -OutputRoot $outputRoot `
    -Version $Version `
    -Publisher $Publisher `
    -Signed ([bool]$CertificateThumbprint) `
    -VerificationStatus $VerificationStatus `
    -EvidencePath $packagedEvidencePath `
    -AppInstallerPath $appInstallerPath

[pscustomobject]@{
    Package = $package
    Signed = [bool]$CertificateThumbprint
    BundledNode = (Get-Item (Join-Path $sidecarStage 'node.exe')).VersionInfo.FileVersion
    ReleaseManifest = $releaseManifestPath
}
