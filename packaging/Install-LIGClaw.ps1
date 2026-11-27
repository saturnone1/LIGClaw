$ErrorActionPreference = "Stop"

$expectedCertificateThumbprint = "DCEE26DFDEB2FE17EBA13462703E745234302ADF"
$packageFileName = "LIGClaw-0.6.0.2-x64.msix"
$certificateFileName = "LIGClaw-Development.cer"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    Write-Host "Requesting Windows administrator permission to install LIGClaw."
    $arguments = @(
        "-NoProfile"
        "-ExecutionPolicy"
        "Bypass"
        "-File"
        ('"{0}"' -f $PSCommandPath)
    )
    Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList $arguments
    exit 0
}

try {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    $buildNumber = [int]$os.BuildNumber
    if ($buildNumber -lt 17763) {
        throw "Windows 10 version 1809 or later is required. Current build: $buildNumber"
    }

    if (-not [Environment]::Is64BitOperatingSystem) {
        throw "This installer requires 64-bit Windows."
    }

    $packagePath = Join-Path $PSScriptRoot $packageFileName
    $certificatePath = Join-Path $PSScriptRoot $certificateFileName
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "The installation package was not found: $packageFileName"
    }
    if (-not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) {
        throw "The public certificate was not found: $certificateFileName"
    }

    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
    $actualThumbprint = $certificate.Thumbprint.Replace(" ", "").ToUpperInvariant()
    if ($actualThumbprint -ne $expectedCertificateThumbprint) {
        throw "The installation certificate does not match this release."
    }
    if ($certificate.Subject -ne "CN=LIGClaw Development") {
        throw "The installation certificate publisher is invalid."
    }

    Write-Host "Registering the LIGClaw public certificate on this computer."
    Import-Certificate -FilePath $certificatePath -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null

    Write-Host "Installing LIGClaw."
    Add-AppxPackage -Path $packagePath -ForceApplicationShutdown

    Write-Host ""
    Write-Host "LIGClaw installation completed." -ForegroundColor Green
    Write-Host "Open LIGClaw from the Windows Start menu."
}
catch {
    Write-Host ""
    Write-Host "Installation failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Review the message above before closing this window."
    Read-Host "Press Enter to close"
    exit 1
}

Read-Host "Press Enter to close"
