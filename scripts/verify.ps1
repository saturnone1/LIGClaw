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
    Invoke-Checked npm ci
    Invoke-Checked npm run check
    Invoke-Checked npm run build
    Invoke-Checked npm test
}
finally {
    Pop-Location
}

Push-Location $repositoryRoot
try {
    Invoke-Checked dotnet format LIGClaw.slnx --verify-no-changes --no-restore
    Invoke-Checked dotnet build LIGClaw.slnx --configuration Debug
    Invoke-Checked dotnet test LIGClaw.slnx --configuration Debug --no-build
}
finally {
    Pop-Location
}
