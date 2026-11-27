[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$parseFailures = New-Object System.Collections.Generic.List[string]

Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'scripts') -Filter '*.ps1' -File | ForEach-Object {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors)
    foreach ($parseError in $errors) {
        $parseFailures.Add("$($_.Name):$($parseError.Extent.StartLineNumber): $($parseError.Message)")
    }
}

if ($parseFailures.Count -gt 0) {
    throw "PowerShell parse failures:`n$($parseFailures -join "`n")"
}

Write-Output "Parsed all PowerShell scripts with Windows PowerShell $($PSVersionTable.PSVersion)."
