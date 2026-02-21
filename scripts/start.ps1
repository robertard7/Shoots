$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$runId = Get-Date -AsUTC -Format 'yyyyMMdd-HHmmss'
$opsRoot = Join-Path $repoRoot "artifacts/ops/$runId"
New-Item -ItemType Directory -Force -Path $opsRoot | Out-Null

$project = Join-Path $repoRoot 'src/Host/Shoots.Host.Smoke/Shoots.Host.Smoke.csproj'
$version = dotnet msbuild $project -nologo -getProperty:Version
$rev = git -C $repoRoot rev-parse HEAD
$dotnetVersion = dotnet --version
$os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription

@{
  os = $os
  dotnet = $dotnetVersion
  repoRev = $rev
  version = $version
  runId = $runId
} | ConvertTo-Json -Depth 3 | Out-File -Encoding utf8 (Join-Path $opsRoot 'env.json')

$lines = @(
  "Repo root: $repoRoot",
  "Version: $version",
  "State sessions: $(Join-Path $repoRoot '.state/chat-intake-sessions.json')",
  "State models: $(Join-Path $repoRoot '.state/models.catalog.json')",
  "Trace pattern: $(Join-Path $repoRoot '.state/trace/<workorder>.trace.json')",
  "Artifacts pattern: $(Join-Path $repoRoot '.state/artifacts/<workorder>/')",
  "Ops root: $opsRoot"
)
$lines | Tee-Object -FilePath (Join-Path $opsRoot 'start.log')

& (Join-Path $repoRoot 'scripts/first_run_check.ps1') | Tee-Object -Append -FilePath (Join-Path $opsRoot 'start.log')
& (Join-Path $repoRoot 'scripts/run_host_smoke.ps1') | Tee-Object -FilePath (Join-Path $opsRoot 'smoke.log')
& (Join-Path $repoRoot 'scripts/run_ui.ps1')
