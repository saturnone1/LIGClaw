[CmdletBinding()]
param([ValidateRange(1, 100)][int]$Restarts = 10)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$executable = (Resolve-Path (Join-Path $repositoryRoot 'src\LIGClaw.Desktop\bin\Debug\net10.0-windows10.0.17763.0\LIGClaw.Desktop.exe')).Path
if (Get-Process -Name LIGClaw.Desktop -ErrorAction SilentlyContinue) { throw '기존 LIGClaw 프로세스를 먼저 종료해 주세요.' }
$desktop = Start-Process -FilePath $executable -WorkingDirectory $repositoryRoot -WindowStyle Hidden -PassThru

function Wait-Sidecar([int]$parentId, [int[]]$excludedIds = @()) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $candidate = Get-CimInstance Win32_Process -Filter "ParentProcessId = $parentId" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq 'node.exe' -and $_.ProcessId -notin $excludedIds } | Select-Object -First 1
    } while (-not $candidate -and [DateTime]::UtcNow -lt $deadline)
    if (-not $candidate) { throw 'Sidecar restart timed out.' }
    return [int]$candidate.ProcessId
}

try {
    $sidecarId = Wait-Sidecar $desktop.Id
    $observed = [System.Collections.Generic.List[int]]::new()
    $observed.Add($sidecarId)
    $desktop.Refresh()
    $initialMemory = $desktop.PrivateMemorySize64
    $initialHandles = $desktop.HandleCount
    for ($index = 0; $index -lt $Restarts; $index++) {
        Stop-Process -Id $sidecarId -Force
        Wait-Process -Id $sidecarId -Timeout 5 -ErrorAction SilentlyContinue
        $sidecarId = Wait-Sidecar $desktop.Id $observed.ToArray()
        $observed.Add($sidecarId)
        if ($desktop.HasExited) { throw 'Desktop exited during restart soak.' }
    }
    Start-Sleep -Seconds 3
    $desktop.Refresh()
    $memoryGrowth = $desktop.PrivateMemorySize64 - $initialMemory
    $handleGrowth = $desktop.HandleCount - $initialHandles
    if ($memoryGrowth -gt 134217728) { throw "Desktop private memory grew by more than 128 MiB: $memoryGrowth" }
    if ($handleGrowth -gt 256) { throw "Desktop handle count grew by more than 256: $handleGrowth" }
    [pscustomobject]@{ Restarts = $Restarts; MemoryGrowthBytes = $memoryGrowth; HandleGrowth = $handleGrowth; Result = 'passed' }
}
finally {
    if (-not $desktop.HasExited) { Stop-Process -Id $desktop.Id -Force }
    $desktop.WaitForExit()
}
