$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$runRoots = @()
if (Test-Path '.state/runs') {
  $runRoots += Get-ChildItem '.state/runs' -Directory | Sort-Object Name
}
if (-not $runRoots -and (Test-Path 'artifacts/builder_loop')) {
  $runRoots += Get-ChildItem 'artifacts/builder_loop' -Directory |
    Sort-Object Name |
    ForEach-Object { Get-ChildItem $_.FullName -Directory -Recurse | Where-Object { $_.FullName -match '\\run\\[^\\]+$' } }
}

if (-not $runRoots) {
  Write-Host 'No run directory found.'
  exit 0
}

$latest = $runRoots[-1].FullName
$targets = @(
  (Join-Path $latest 'run_summary.md'),
  (Join-Path $latest 'narration/events.ndjson'),
  (Join-Path $latest 'retrieval/result.json'),
  (Join-Path $latest 'retrieval/scoring.ndjson'),
  (Join-Path $latest 'slice/decisions.ndjson'),
  (Join-Path $latest 'plan_synthesis/result.json'),
  (Join-Path $latest 'plan_synthesis/evidence.ndjson'),
  (Join-Path $latest 'plan/plan.json')
)

foreach ($path in $targets) {
  if (Test-Path $path) {
    Start-Process $path | Out-Null
  }
}

Write-Host "Opened latest run: $latest"
