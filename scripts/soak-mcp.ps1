[CmdletBinding()]
param([ValidateRange(1, 200)][int]$Iterations = 20)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
npm run build --prefix (Join-Path $repositoryRoot 'sidecar')
if ($LASTEXITCODE -ne 0) { throw 'Sidecar build failed.' }
node --expose-gc (Join-Path $repositoryRoot 'sidecar\test\mcp-soak.js') $Iterations
if ($LASTEXITCODE -ne 0) { throw 'MCP soak failed.' }
