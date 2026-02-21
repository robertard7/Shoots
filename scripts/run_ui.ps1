param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$uiProject = Join-Path $repoRoot 'ui/Shoots.Ui/Shoots.Ui.csproj'
$logRoot = Join-Path $repoRoot 'artifacts/ui/logs'
$artifactRoot = Join-Path $repoRoot 'artifacts/ui/output'
$stateRoot = Join-Path $repoRoot '.state'
$tracePattern = Join-Path $stateRoot 'trace/<workorder>.trace.json'
$artifactsPattern = Join-Path $stateRoot 'artifacts/<workorder>/'
$uiBuildOutput = Join-Path $repoRoot "ui/Shoots.Ui/bin/$Configuration/net8.0-windows"

New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

Write-Host "Repo root: $repoRoot"
Write-Host "Configuration: $Configuration"
Write-Host "UI build output: $uiBuildOutput"
Write-Host ".state root: $stateRoot"
Write-Host "Trace file pattern: $tracePattern"
Write-Host "Artifacts folder pattern: $artifactsPattern"
Write-Host "Trace logs: $logRoot"
Write-Host "Artifacts: $artifactRoot"

Push-Location $repoRoot
try {
    dotnet restore $uiProject
    dotnet build $uiProject -c $Configuration --no-restore

    $env:SHOOTS_UI_LOG_ROOT = $logRoot
    $env:SHOOTS_UI_ARTIFACT_ROOT = $artifactRoot

    dotnet run --project $uiProject -c $Configuration --no-build
}
finally {
    Pop-Location
}
