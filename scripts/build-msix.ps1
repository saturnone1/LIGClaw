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
$outputRoot = Join-Path $repositoryRoot "artifacts\msix\$Version"
$stage = Join-Path $outputRoot 'stage'
$package = Join-Path $outputRoot "LIGClaw-$Version-x64.msix"
if (Test-Path -LiteralPath $outputRoot) { throw "출력 폴더가 이미 있습니다: $outputRoot" }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

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
Set-Content -LiteralPath (Join-Path $stage 'AppxManifest.xml') -Value $manifest -Encoding utf8NoBOM

$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10\bin'
$makeAppx = Get-ChildItem $sdkRoot -Recurse -Filter makeappx.exe | Where-Object FullName -Match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeAppx) { throw 'Windows SDK MakeAppx.exe를 찾지 못했습니다.' }
& $makeAppx.FullName pack /d $stage /p $package
if ($LASTEXITCODE -ne 0) { throw 'MSIX packaging failed.' }

if ($CertificateThumbprint) {
    $signTool = Get-ChildItem $sdkRoot -Recurse -Filter signtool.exe | Where-Object FullName -Match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signTool) { throw 'Windows SDK SignTool.exe를 찾지 못했습니다.' }
    $arguments = @('sign', '/fd', 'SHA256', '/sha1', $CertificateThumbprint)
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
    Set-Content -LiteralPath (Join-Path $outputRoot 'LIGClaw.appinstaller') -Value $appInstaller -Encoding utf8NoBOM
}

[pscustomobject]@{ Package = $package; Signed = [bool]$CertificateThumbprint; BundledNode = (Get-Item (Join-Path $sidecarStage 'node.exe')).VersionInfo.FileVersion }
