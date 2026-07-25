[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath exited with code $LASTEXITCODE."
    }
}

Push-Location (Join-Path $repositoryRoot 'sidecar')
try {
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'sidecar/dist/index.js'))) {
        Invoke-Checked npm ci
    }
    Invoke-Checked npm run build
}
finally {
    Pop-Location
}

Invoke-Checked dotnet run --project (Join-Path $repositoryRoot 'src/LIGClaw.Desktop/LIGClaw.Desktop.csproj')
