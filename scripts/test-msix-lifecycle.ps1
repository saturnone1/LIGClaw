[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InitialPackage,
    [Parameter(Mandatory)][string]$UpdatePackage
)

$ErrorActionPreference = 'Stop'
$initial = (Resolve-Path -LiteralPath $InitialPackage).Path
$update = (Resolve-Path -LiteralPath $UpdatePackage).Path
$installed = Get-AppxPackage -Name LIGClaw -ErrorAction SilentlyContinue
if ($installed) { throw 'LIGClaw MSIX가 이미 설치되어 있습니다. 테스트 전 사용자 상태를 정리해 주세요.' }

try {
    Add-AppxPackage -Path $initial
    $first = Get-AppxPackage -Name LIGClaw -ErrorAction Stop
    Start-Process "shell:AppsFolder\$($first.PackageFamilyName)!LIGClaw"
    Start-Sleep -Seconds 2
    if (-not (Get-Process -Name LIGClaw.Desktop -ErrorAction SilentlyContinue)) { throw '설치된 앱이 실행되지 않았습니다.' }
    Get-Process -Name LIGClaw.Desktop -ErrorAction SilentlyContinue | Stop-Process

    Add-AppxPackage -Path $update
    $second = Get-AppxPackage -Name LIGClaw -ErrorAction Stop
    if ([version]$second.Version -le [version]$first.Version) { throw '상위 버전 업데이트가 적용되지 않았습니다.' }
}
finally {
    Get-Process -Name LIGClaw.Desktop -ErrorAction SilentlyContinue | Stop-Process
    $package = Get-AppxPackage -Name LIGClaw -ErrorAction SilentlyContinue
    if ($package) { Remove-AppxPackage -Package $package.PackageFullName -Confirm:$false }
}

if (Get-AppxPackage -Name LIGClaw -ErrorAction SilentlyContinue) { throw '패키지가 제거되지 않았습니다.' }
[pscustomobject]@{ Install = 'passed'; Launch = 'passed'; Update = 'passed'; Remove = 'passed' }
