$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repoRoot 'src/Host/Shoots.Host.Smoke/Shoots.Host.Smoke.csproj'
$version = dotnet msbuild $project -nologo -getProperty:Version
$stateRoot = Join-Path $repoRoot '.state'
$tracePattern = Join-Path $stateRoot 'trace/<workorder>.trace.json'
$artifactsPattern = Join-Path $stateRoot 'artifacts/<workorder>/'
$sessionsPath = Join-Path $stateRoot 'chat-intake-sessions.json'
$modelsPath = Join-Path $stateRoot 'models.catalog.json'
$smokeOutputRoot = Join-Path $repoRoot 'artifacts/smoke/local'

New-Item -ItemType Directory -Force -Path $smokeOutputRoot | Out-Null

Write-Host "Repo root: $repoRoot"
Write-Host "Version: $version"
Write-Host ".state sessions: $sessionsPath"
Write-Host ".state models catalog: $modelsPath"
Write-Host "Trace pattern: $tracePattern"
Write-Host "Artifacts pattern: $artifactsPattern"
Write-Host "Smoke output root: $smokeOutputRoot"
Write-Host "Tools mode: embedded deterministic execution"

dotnet run --project $project -c Release -- ChatIntakeSmoke | Tee-Object -FilePath (Join-Path $smokeOutputRoot 'smoke.log')
