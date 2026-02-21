param(
    [Alias("PreserveModelsCatalog")]
    [switch]$KeepModels
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stateRoot = Join-Path $repoRoot '.state'

$traceRoot = Join-Path $stateRoot 'trace'
$artifactsRoot = Join-Path $stateRoot 'artifacts'
$sessionsPath = Join-Path $stateRoot 'chat-intake-sessions.json'
$modelsPath = Join-Path $stateRoot 'models.catalog.json'

if (Test-Path $traceRoot) { Remove-Item -Recurse -Force $traceRoot }
if (Test-Path $artifactsRoot) { Remove-Item -Recurse -Force $artifactsRoot }
if (Test-Path $sessionsPath) { Remove-Item -Force $sessionsPath }

if (-not $KeepModels -and (Test-Path $modelsPath)) {
    Remove-Item -Force $modelsPath
}

Write-Host "Cleaned state under: $stateRoot"
Write-Host "Preserved models catalog: $KeepModels"
